#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MORLA XR 位姿对比探针 —— 手（wrist）与手柄（controller）的发送频率、帧间隔
与坐标系方向对照。

默认监听 8080/TCP（和你的要求一致）。注意 App 里位姿默认发到 posePort：
有线 TCP 8000 / 无线 TCP 8000 / UDP 9000；要么把「设置 → 连接与传输」的端口
改成 8080，要么就用 `adb reverse tcp:8080 tcp:8080` 把设备上的 8080 转发到本机
（前者是改 App 配置，后者是改端口映射，二选一）。`--port` 可改。

用法：
    python Tools/morla_pose_compare.py --port 8080
    # 戴上头显、左手/右手做动作：
    #   第 1 段：用手 —— 前后、左右、pitch（点头）、yaw（摇头）、roll（歪头）
    #   第 2 段：拿手柄做同样的动作
    # 每换一段，在跑的终端里按一次回车（回车 = 切到下一阶段）。
    # Ctrl+C 结束时打印汇总，并写出 CSV。

输出：
  * 每秒一张表：每个源的真实 Hz（1 秒窗口）、帧间隔中位数、序号缺口、
    最新位置与欧拉角，以及相对本阶段第一个样本的位移/转角。
  * 结束时按阶段汇总：位置与欧拉角的范围、位移方向，用于判断手和手柄
    的坐标系方向是否一致（同一动作 → 同一个符号）。
  * CSV：每帧一条记录，阶段号、源、位置、四元数。

只依赖标准库，不需要 numpy。
"""

import argparse
import math
import os
import re
import socket
import sys
import threading
import time
from collections import defaultdict, deque

try:                                   # Windows 控制台默认 GBK
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ENVELOPE = re.compile(r'^@q3m (\S+) (\d+) (\d+)\|(.*)$')
FLOATS = re.compile(r'[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?')
POSE_LABELS = ('Left wrist', 'Right wrist', 'Left controller', 'Right controller', 'Head pose', 'Body hips')
BUTTON_LABELS = ('Left buttons', 'Right buttons')
WINDOW_S = 1.0
HISTORY = 600                          # 每源保留最近 600 帧用于统计

lock = threading.Lock()
latest = {}                            # label -> (pos, quat, t)
counts = defaultdict(int)
arrivals = defaultdict(lambda: deque(maxlen=HISTORY))
intervals = defaultdict(lambda: deque(maxlen=HISTORY))   # 本地到达间隔 ms
motion = defaultdict(lambda: deque(maxlen=HISTORY * 2))  # 位姿真正变化的时刻
label_conn = {}                        # label -> 该标签所在连接的序号统计
buttons = {}                           # label -> (trigger, grip, sx, sy, mask)
MOVE_EPS = 0.0002                      # 0.2 mm
ANGLE_EPS = 0.1                        # 0.1°
phases = []                            # [{'name','start','end','rows':[]}]
csv_rows = []                          # 简化：结束时一次性写出


def _new_phase(name=''):
    phase = {'name': name or ('阶段 %d' % (len(phases) + 1)), 'start': time.time(),
             'end': None, 'rows': []}
    if phases and phases[-1]['end'] is None:
        phases[-1]['end'] = phase['start']
    phases.append(phase)
    return phase


# ---------------------------------------------------------------------------
# 四元数 / 欧拉角（Unity 习惯：ZXY 顺序，角度制）
# ---------------------------------------------------------------------------

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by + ay * bw + az * bx - ax * bz,
            aw * bz + az * bw + ax * by - ay * bx,
            aw * bw - ax * bx - ay * by - az * bz)


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qnormalize(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def qeuler(q):
    """返回 (pitch, yaw, roll)，单位度，与 Unity Quaternion.eulerAngles 同序。"""
    x, y, z, w = qnormalize(q)
    r02 = 2.0 * (x * z + w * y)
    r12 = 2.0 * (y * z - w * x)
    r22 = 1.0 - 2.0 * (x * x + y * y)
    r10 = 2.0 * (x * y + w * z)
    r11 = 1.0 - 2.0 * (x * x + z * z)
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, -r12))))
    yaw = math.degrees(math.atan2(r02, r22))
    roll = math.degrees(math.atan2(r10, r11))
    return pitch, yaw, roll


def qrelative(q0, qt):
    return qmul(qconj(q0), qt)


def qangle(a, b):
    """两个单位四元数之间的夹角（度）。"""
    dot = abs(sum(a[i] * b[i] for i in range(4)))
    return math.degrees(2.0 * math.acos(max(-1.0, min(1.0, dot))))


def pose_changed(previous, pos, quat):
    """上一帧与本帧是否有真实变化——用来区分"没发"和"发了但数据没更新"。"""
    ppos, pquat, _ = previous
    if any(abs(pos[i] - ppos[i]) > MOVE_EPS for i in range(3)):
        return True
    return qangle(pquat, quat) > ANGLE_EPS


# ---------------------------------------------------------------------------
# 解析
# ---------------------------------------------------------------------------

def track(label, conn, now):
    with lock:
        counts[label] += 1
        if arrivals[label]:
            dt = (now - arrivals[label][-1]) * 1000.0
            if 0.0 <= dt <= 2000.0:
                intervals[label].append(dt)
        arrivals[label].append(now)
        if conn is not None:
            label_conn[label] = conn


def update_seq(conn, seq):
    """序号是**每条连接**自增的（手柄连接里 controller / buttons 各占一个序号），
    所以只能按连接判缺口，按标签判会把 'controller→buttons→controller' 的 +2 当成丢包。"""
    if seq is None:
        return
    with lock:
        last = conn.get('last_seq')
        if last is not None and seq > last + 1:
            conn['missing'] += seq - last - 1
        if last is None or seq > last:
            conn['last_seq'] = seq


def handle_line(raw, conn):
    raw = raw.strip('\r\n ')
    if not raw:
        return
    m = ENVELOPE.match(raw)
    seq = None
    if m:
        seq = int(m.group(2))
        raw = m.group(4)
    update_seq(conn, seq)
    now = time.time()

    if raw.startswith('body iobt'):
        # `body iobt | fid=High: hips,x,y,z,qx,qy,qz,qw spine-lower,...`
        # 取 hips 当作身体位姿参与统计（各关节是同一帧发的，用 hips 代表整条流的频率）。
        track('body', conn, now)
        _, _, joints = raw.partition(':')
        for token in joints.split():
            if not token.startswith('hips,'):
                continue
            values = [float(v) for v in FLOATS.findall(token)]
            if len(values) < 7:
                break
            pos = tuple(values[0:3])
            quat = qnormalize(tuple(values[3:7]))
            with lock:
                latest['Body hips'] = (pos, quat, now)
                if phases:
                    phases[-1]['rows'].append(('Body hips', pos, quat, now))
                    csv_write(len(phases), phases[-1]['name'], 'Body hips', pos, quat, now)
            track('Body hips', conn, now)
            break
        return

    label, _, rest = raw.partition(':')
    if ' | ' in label:                 # `Left wrist | f = 1 | t = 2:` 调试头
        label = label.split(' | ')[0]
    # 线上两种写法都有：调试头开着是 `Left wrist | f = 1 | t = 2:`（无逗号），
    # 关掉时是 `Left wrist:, ...`；手柄行一律带逗号（`Left controller:, ...`）。
    label = label.strip().strip(',').strip()
    if not label:
        return

    if label in POSE_LABELS:
        vals = [float(v) for v in FLOATS.findall(rest)]
        if len(vals) < 7:
            return
        pos = tuple(vals[0:3])
        quat = qnormalize(tuple(vals[3:7]))
        # track() 自己会拿 lock —— 绝不能放在 with lock 里面，否则第一帧就死锁。
        with lock:
            previous = latest.get(label)
            latest[label] = (pos, quat, now)
            if phases:
                phases[-1]['rows'].append((label, pos, quat, now))
                csv_write(len(phases), phases[-1]['name'], label, pos, quat, now)
            if previous is None or pose_changed(previous, pos, quat):
                motion[label].append(now)
        track(label, conn, now)
        return

    if label in BUTTON_LABELS:
        vals = [float(v) for v in FLOATS.findall(rest)]
        if len(vals) >= 5:
            with lock:
                buttons[label] = vals[:5]
            track(label, conn, now)


# 边收边写：进程被 TaskStop / 关窗口杀掉时 finally 不会执行，只在结束时写会丢全部数据。
csv_handle = [None]
csv_started = [0.0]


def csv_write(index, name, label, pos, quat, now):
    handle = csv_handle[0]
    if handle is None:
        return
    handle.write('%d,%s,%.3f,%s,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f\n' % (
        index, name, now, label, pos[0], pos[1], pos[2], quat[0], quat[1], quat[2], quat[3]))


def split_buffer(buf):
    """返回 (完整行列表, 剩余字节)。"""
    parts = buf.split(b'\n')
    return parts[:-1], parts[-1]


# ---------------------------------------------------------------------------
# 网络
# ---------------------------------------------------------------------------

conn_count = [0]
# TCP 客户端实际连到哪一侧不重要，app 是主动连过来；我们只做 server。
def serve(sock, stop):
    while not stop.is_set():
        try:
            conn, addr = sock.accept()
        except OSError:
            return
        conn_count[0] += 1
        threading.Thread(target=read_conn, args=(conn, addr, stop), daemon=True).start()


def read_conn(conn, addr, stop):
    conn.settimeout(1.0)
    buf = b''
    stats = {'last_seq': None, 'missing': 0, 'peer': '%s:%d' % (addr[0], addr[1])}
    try:
        while not stop.is_set():
            try:
                chunk = conn.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
            lines, buf = split_buffer(buf)
            for line in lines:
                try:
                    handle_line(line.decode('utf-8', 'replace'), stats)
                except Exception:
                    pass
    finally:
        conn_count[0] -= 1
        try:
            conn.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# 统计与输出
# ---------------------------------------------------------------------------

def window_hz(label, now):
    marks = arrivals[label]
    live = [t for t in marks if now - t <= WINDOW_S]
    return len(live) / WINDOW_S


def motion_hz(label, now):
    marks = motion[label]
    live = [t for t in marks if now - t <= WINDOW_S]
    return len(live) / WINDOW_S


def median(values):
    if not values:
        return float('nan')
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def phase_snapshot(index):
    with lock:
        rows = list(phases[index]['rows']) if index < len(phases) else []
    first = {}
    for label, pos, quat, _ in rows:
        if label not in first:
            first[label] = (pos, quat)
    return rows, first


def print_live(index):
    now = time.time()
    rows, first = phase_snapshot(index)
    name = phases[index]['name'] if index < len(phases) else '-'
    age = now - phases[index]['start'] if index < len(phases) else 0.0
    print('\n── [%s] %s  已 %.1fs  连接 %d ──' % (
        time.strftime('%H:%M:%S'), name, age, max(conn_count[0], 0)))
    print('  %-17s %6s %7s %6s %6s  %-24s %-22s %s' % (
        '源', 'Hz', '间隔ms', '变化Hz', '连接丢', '位置 x,y,z', 'Δ位置 自阶段起点', 'Δ欧拉 pitch,yaw,roll'))
    for label in POSE_LABELS + BUTTON_LABELS:
        if counts.get(label, 0) == 0:
            continue
        hz = window_hz(label, now)
        gap = median(list(intervals.get(label, ()))[-90:])
        owner = label_conn.get(label)
        missing = owner['missing'] if owner is not None else 0
        entry = latest.get(label)
        if entry is None:
            b = buttons.get(label)
            extra = '' if b is None else 'T%.2f G%.2f S(%.2f,%.2f) m%d' % tuple(b[:4] + [int(b[4])])
            print('  %-17s %6.1f %7s %6s %6d  %s' % (
                label, hz, '-' if math.isnan(gap) else '%.1f' % gap, '-', missing, extra))
            continue
        pos, quat, _ = entry
        delta = ''
        rot = ''
        if label in first:
            p0, q0 = first[label]
            d = tuple(pos[i] - p0[i] for i in range(3))
            e = qeuler(qrelative(q0, quat))
            delta = '(%+.3f,%+.3f,%+.3f)' % d
            rot = '(%+.1f,%+.1f,%+.1f)' % e
        print('  %-17s %6.1f %7s %6.1f %6d  (%+.3f,%+.3f,%+.3f) %-22s %s' % (
            label, hz, '-' if math.isnan(gap) else '%.1f' % gap, motion_hz(label, now), missing,
            pos[0], pos[1], pos[2], delta, rot))


def summarize(label, rows):
    pts = [(pos, quat) for name, pos, quat, _ in rows if name == label]
    if len(pts) < 2:
        return None
    lo = [min(p[0][i] for p in pts) for i in range(3)]
    hi = [max(p[0][i] for p in pts) for i in range(3)]
    p0, q0 = pts[0]
    pend, qend = pts[-1]
    e0 = qeuler(q0)
    erel = qeuler(qrelative(q0, qend))
    eul = [qeuler(q) for _, q in pts]
    elo = [min(e[i] for e in eul) for i in range(3)]
    ehi = [max(e[i] for e in eul) for i in range(3)]
    changes = sum(1 for i in range(1, len(pts))
                  if pose_changed((pts[i - 1][0], pts[i - 1][1], 0.0), pts[i][0], pts[i][1]))
    return {
        'n': len(pts), 'changes': changes, 'samples': pts,
        'pos_range': [hi[i] - lo[i] for i in range(3)],
        'pos_delta': [pend[i] - p0[i] for i in range(3)],
        'euler_start': e0, 'euler_delta_rel': erel,
        'euler_range': [ehi[i] - elo[i] for i in range(3)],
    }


def sign(v, eps):
    return '-' if v < -eps else ('+' if v > eps else '·')


def print_final():
    print('\n' + '=' * 78)
    print('阶段汇总')
    print('=' * 78)
    for index, phase in enumerate(phases):
        rows = phase['rows']
        if not rows:
            print('\n[%s] 没有数据' % phase['name'])
            continue
        dur = (phase['end'] or time.time()) - phase['start']
        seen = defaultdict(int)
        for name, _, _, _ in rows:
            seen[name] += 1
        print('\n[阶段 %d] %s   时长 %.1fs   总帧 %d' % (index + 1, phase['name'], dur, len(rows)))
        for label in POSE_LABELS:
            if not seen.get(label):
                continue
            s = summarize(label, rows)
            if s is None:
                continue
            hz = seen[label] / dur if dur > 0 else 0.0
            chz = s['changes'] / dur if dur > 0 else 0.0
            print('  %-17s %5d 帧  %5.1f Hz   位姿实际变化 %d 次 / %.1f Hz' % (
                label, s['n'], hz, s['changes'], chz))
            print('      位置范围  x %.3f  y %.3f  z %.3f  m' % tuple(s['pos_range']))
            print('      位移      x %+.3f  y %+.3f  z %+.3f  m   (末帧 - 首帧)' % tuple(s['pos_delta']))
            print('      欧拉范围  pitch %.1f°  yaw %.1f°  roll %.1f°' % tuple(s['euler_range']))
            print('      相对转角  pitch %+.1f°  yaw %+.1f°  roll %+.1f°' % tuple(s['euler_delta_rel']))

    # 方向对照：同一阶段里手和手柄都有数据时才比；不同阶段则按"最大位移轴"比。
    print('\n' + '-' * 78)
    print('方向对照（看符号是否一致）')
    print('-' * 78)
    for index, phase in enumerate(phases):
        for label in ('Left wrist', 'Left controller', 'Right wrist', 'Right controller'):
            s = summarize(label, phase['rows'])
            if s is None:
                continue
            d = s['pos_delta']
            e = s['euler_delta_rel']
            big = max(range(3), key=lambda i: abs(d[i]))
            print('  阶段%d %-16s 主轴 %s %+.3f m（x %+.3f  y %+.3f  z %+.3f）  |  pitch %+.1f°  yaw %+.1f°  roll %+.1f°（%s）' % (
                index + 1, label, 'xyz'[big], d[big], d[0], d[1], d[2], e[0], e[1], e[2],
                ''.join(sign(e[i], 3) for i in range(3))))


def csv_open(path):
    try:
        handle = open(path, 'w', encoding='utf-8', newline='')
        handle.write('phase,phase_name,t,label,x,y,z,qx,qy,qz,qw\n')
        csv_handle[0] = handle
        csv_started[0] = time.time()
        return path
    except OSError as exc:
        print('CSV 打开失败：%s' % exc)
        return None


def csv_close():
    handle = csv_handle[0]
    if handle is None:
        return
    csv_handle[0] = None
    try:
        handle.flush()
        handle.close()
    except OSError:
        pass


def main():
    parser = argparse.ArgumentParser(description='MORLA XR 手/手柄位姿对比探针')
    parser.add_argument('--port', type=int, default=8080, help='监听端口（默认 8080）')
    parser.add_argument('--host', default='0.0.0.0', help='监听地址')
    parser.add_argument('--seconds', type=float, default=0, help='>0 时到点自动结束')
    parser.add_argument('--csv', default='', help='CSV 输出路径（默认 Tools/pose_compare_<时间>.csv）')
    parser.add_argument('--no-input', action='store_true', help='不读键盘：阶段用 --phase-seconds 自动切')
    parser.add_argument('--phase-seconds', type=float, default=0, help='与 --no-input 配合：每段时长（秒）')
    args = parser.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((args.host, args.port))
    sock.listen(16)
    stop = threading.Event()
    threading.Thread(target=serve, args=(sock, stop), daemon=True).start()

    _new_phase('阶段 1')
    csv_path = args.csv or os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        'pose_compare_%s.csv' % time.strftime('%Y%m%d_%H%M%S'))
    csv_open(csv_path)
    print('监听 %s:%d —— 请在 App 里把位姿端口设为 %d（或用 adb reverse 映射）。' % (
        args.host, args.port, args.port))
    print('CSV（边收边写）：%s' % csv_path)
    print('动作顺序建议：先用手做 前后 / 左右 / pitch / yaw / roll，按回车切段，再用手柄做一遍。')
    print('按回车 = 切到下一阶段；Ctrl+C = 结束并汇总。\n')

    def keyboard():
        while not stop.is_set():
            try:
                line = sys.stdin.readline()
            except Exception:
                return
            if line == '':
                return                      # stdin 关闭（重定向/后台运行）
            if stop.is_set():
                return
            name = line.strip()
            with lock:
                _new_phase(name)
                index = len(phases) - 1
            print('\n>>> 切到阶段 %d%s' % (index + 1, ('：' + name) if name else ''))

    if not args.no_input:
        threading.Thread(target=keyboard, daemon=True).start()

    started = time.time()
    next_phase_at = started + args.phase_seconds if args.phase_seconds > 0 else None
    try:
        while True:
            time.sleep(WINDOW_S)
            if stop.wait(0):
                break
            with lock:
                index = len(phases) - 1
            print_live(index)
            if csv_handle[0] is not None:
                try:
                    csv_handle[0].flush()          # 强杀也不丢已收到的帧
                except (OSError, ValueError):
                    pass
            if args.phase_seconds > 0 and next_phase_at is not None and time.time() >= next_phase_at:
                with lock:
                    _new_phase()
                print('\n>>> 自动切到阶段 %d' % len(phases))
                next_phase_at = time.time() + args.phase_seconds
            if args.seconds > 0 and time.time() - started >= args.seconds:
                break
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        with lock:
            if phases and phases[-1]['end'] is None:
                phases[-1]['end'] = time.time()
            print_final()
            csv_close()
            print('\nCSV：%s' % csv_path)
        try:
            sock.close()
        except OSError:
            pass


if __name__ == '__main__':
    main()
