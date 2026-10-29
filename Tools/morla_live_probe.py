#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MORLA XR 实时探针 —— 同时接收位姿(8000/TCP)与语音(8001/TCP)，统计 + 可视化。

与仓库里的 skeleton_viewer.py 的区别：
  * 认 1.1 的 `@q3m <session> <seq> <monotonic_us>|` 包头（旧脚本不认，会解析不出任何东西）
  * 同一端口并发接多路 TCP（App 是每个源一条连接：left / right / controller / head / body）
  * 额外收语音 8001 的 `VoicePCM:16000:<base64>`，解 PCM16 出电平与波形
  * 可选：连上后主动发 `@q3sync` 挑战，验证 App 的校时应答链路
  * 控制台每秒打印一次真实统计（帧率 / 序号连续性 / 帧间隔），GUI 只是辅助

用法：
    adb reverse tcp:8000 tcp:8000
    adb reverse tcp:8001 tcp:8001
    python Tools/morla_live_probe.py                # GUI + 控制台
    python Tools/morla_live_probe.py --no-gui       # 只打印统计
    python Tools/morla_live_probe.py --seconds 60   # 60 秒后自动退出

WebRTC 视频(8765)不在这里：那需要一个真正的信令/媒体服务端。
"""

import argparse
import base64
import os
import re
import socket
import sys
import threading
import time
from collections import defaultdict, deque

import numpy as np

try:                                   # Windows 控制台默认 GBK，中文会乱码
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ENVELOPE = re.compile(r'^@q3m (\S+) (\d+) (\d+)\|(.*)$')
FLOATS = re.compile(r'[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?')
POSE_LABELS = ('Left wrist', 'Right wrist', 'Left controller', 'Right controller', 'Head pose')
WINDOW_S = 1.0

lock = threading.Lock()
state = {'body': {}, 'hips': None, 'head': None, 'left_hand': None, 'right_hand': None,
         'left_ctrl': None, 'right_ctrl': None}
counts = defaultdict(int)                       # label -> 累计帧数
arrivals = defaultdict(lambda: deque(maxlen=400))  # label -> 最近到达时间(本地秒)
gaps = defaultdict(lambda: deque(maxlen=400))   # label -> 包内 monotonic_us 差值(ms)
seqs = {}                                       # label -> (last_seq, missing, session)
session_ids = set()
sync = {'challenges': 0, 'acks': 0, 'rtt_ms': deque(maxlen=20)}
voice = {'frames': 0, 'samples': 0, 'rate': 0, 'rms': 0.0, 'peak': 0.0,
         'chunks': deque(maxlen=50), 'last': 0.0, 'pcm': bytearray(),
         'win_frames': 0, 'win_samples': 0, 'win_peak': 0.0, 'win_energy': 0.0, 'win_active': 0}
connections = {'pose': 0, 'voice': 0}
WAV_CAP_BYTES = 16000 * 2 * 600          # 最多留 10 分钟
WAV_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'voice_capture.wav')


def window_hz(label, now):
    marks = arrivals[label]
    while marks and now - marks[0] > WINDOW_S:
        marks.popleft()
    return len(marks) / WINDOW_S


# ---------------------------------------------------------------------------
# 解析
# ---------------------------------------------------------------------------

def split_envelope(raw):
    """返回 (session, seq, monotonic_us, payload)。没有包头时前三项为 None。"""
    m = ENVELOPE.match(raw)
    if m:
        return m.group(1), int(m.group(2)), int(m.group(3)), m.group(4)
    return None, None, None, raw


def track(label, seq, us, now):
    """记录序号连续性与包间隔（包内 monotonic_us 差值，单位 ms）。"""
    with lock:
        counts[label] += 1
        arrivals[label].append(now)
        last_seq, missing, prev_us = seqs.get(label, (None, 0, None))
        if seq is not None:
            if last_seq is not None and seq > last_seq + 1:
                missing += seq - last_seq - 1
            if prev_us is not None and us is not None:
                delta = (us - prev_us) / 1000.0
                if 0.0 <= delta <= 500.0:
                    gaps[label].append(delta)
            if last_seq is None or seq > last_seq:
                last_seq = seq
            if us is not None:
                prev_us = us
        seqs[label] = (last_seq, missing, prev_us)


def handle_pose_line(raw):
    raw = raw.strip()
    if not raw:
        return
    session, seq, us, line = split_envelope(raw)
    now = time.time()
    if session:
        session_ids.add(session)

    if line.startswith('body iobt'):
        label = 'body'
        _, _, rest = line.partition(':')
        joints = {}
        for token in rest.split():
            parts = token.split(',')
            if len(parts) != 8:
                continue
            try:
                vals = [float(v) for v in parts[1:]]
            except ValueError:
                continue
            joints[parts[0]] = (np.array(vals[0:3]), np.array(vals[3:7]))
        if joints:
            with lock:
                state['body'] = joints
                state['hips'] = joints.get('hips')
        track(label, seq, us, now)
        return

    label, _, rest = line.partition(':')
    if ' | ' in label:                 # 协议调试头 `Left wrist | f = 1 | t = 2:`
        label = label.split(' | ')[0]
    label = label.strip()

    if label in POSE_LABELS:
        vals = [float(x) for x in FLOATS.findall(rest)]
        if len(vals) < 7:
            return
        pos, quat = np.array(vals[0:3]), np.array(vals[3:7])
        with lock:
            if label == 'Head pose':
                state['head'] = (pos, quat)
            elif label == 'Left controller':
                state['left_ctrl'] = (pos, quat)
            elif label == 'Right controller':
                state['right_ctrl'] = (pos, quat)
            elif label == 'Left wrist':
                state['left_hand'] = (pos, quat, state['left_hand'][2] if state['left_hand'] else None)
            elif label == 'Right wrist':
                state['right_hand'] = (pos, quat, state['right_hand'][2] if state['right_hand'] else None)
        track(label, seq, us, now)
    elif label in ('Left landmarks', 'Right landmarks'):
        vals = [float(x) for x in FLOATS.findall(rest)]
        if len(vals) < 63:
            return
        pts = np.array(vals[0:63]).reshape(21, 3)
        with lock:
            key = 'left_hand' if label == 'Left landmarks' else 'right_hand'
            if state[key] is None:
                state[key] = (np.zeros(3), np.array([0.0, 0.0, 0.0, 1.0]), pts)
            else:
                pos, quat, _ = state[key]
                state[key] = (pos, quat, pts)
        track(label, seq, us, now)


def handle_voice_line(raw):
    raw = raw.strip()
    if not raw or not raw.startswith('@q3m') and 'VoicePCM' not in raw:
        return
    _, _, _, line = split_envelope(raw)
    if not line.startswith('VoicePCM:'):
        return
    try:
        _, rate_text, payload = line.split(':', 2)
        pcm = base64.b64decode(payload)
        rate = int(rate_text)
    except Exception:
        return
    samples = np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768.0
    if samples.size == 0:
        return
    peak = float(np.max(np.abs(samples)))
    with lock:
        voice['frames'] += 1
        voice['samples'] += samples.size
        voice['rate'] = rate
        voice['rms'] = float(np.sqrt(np.mean(samples * samples)))
        voice['peak'] = peak
        voice['last'] = time.time()
        voice['chunks'].append(samples)
        # 整段滚存：判断“有没有真的收到人声”要能听/转写，只看单帧不够
        if len(voice['pcm']) < WAV_CAP_BYTES:
            voice['pcm'].extend(pcm)
        voice['win_frames'] += 1
        voice['win_samples'] += samples.size
        voice['win_peak'] = max(voice['win_peak'], peak)
        voice['win_energy'] += float(np.dot(samples, samples))
        voice['win_active'] += int(np.count_nonzero(np.abs(samples) > 0.001))


def write_wav(path, data, rate=16000):
    """整段重写（头部字节数需要最终长度），先写临时文件再替换。"""
    import wave
    tmp = path + '.tmp'
    with wave.open(tmp, 'wb') as out:
        out.setnchannels(1); out.setsampwidth(2); out.setframerate(rate)
        out.writeframes(bytes(data))
    os.replace(tmp, path)


# ---------------------------------------------------------------------------
# 网络
# ---------------------------------------------------------------------------

def serve(port, handler, kind, challenge=False):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((BIND_HOST, port))
    server.listen(16)
    print('[+] 监听 %s:%d (%s)' % (BIND_HOST, port, kind), flush=True)
    while True:
        conn, addr = server.accept()
        with lock:
            connections[kind] += 1
        print('[+] %s 新连接 %s:%d' % (kind, addr[0], addr[1]), flush=True)
        threading.Thread(target=read_connection, args=(conn, addr, handler, kind, challenge), daemon=True).start()


def read_connection(conn, addr, handler, kind, challenge):
    buf = b''
    sent_at = None
    try:
        conn.settimeout(None)
        if challenge:
            token = '%08x' % int(time.time() * 1000)
            sent_at = time.time()
            conn.sendall(('@q3sync %s %d\n' % (token, int(time.time() * 1e9))).encode('ascii'))
            with lock:
                sync['challenges'] += 1
        while True:
            chunk = conn.recv(65536)
            if not chunk:
                break
            buf += chunk
            while b'\n' in buf:
                raw, buf = buf.split(b'\n', 1)
                text = raw.decode('utf-8', 'replace')
                if text.startswith('@q3sync_ack'):
                    with lock:
                        sync['acks'] += 1
                        if sent_at:
                            sync['rtt_ms'].append((time.time() - sent_at) * 1000)
                    continue
                handler(text)
    except Exception as error:
        print('[!] %s 连接异常 %s: %s' % (kind, addr, error), flush=True)
    finally:
        try:
            conn.close()
        except OSError:
            pass
        with lock:
            connections[kind] = max(0, connections[kind] - 1)
        print('[-] %s 断开 %s:%d' % (kind, addr[0], addr[1]), flush=True)


# ---------------------------------------------------------------------------
# 控制台统计
# ---------------------------------------------------------------------------

PORTS = {'pose': 8000, 'voice': 8001}
BIND_HOST = '127.0.0.1'


def report():
    now = time.time()
    with lock:
        rows = []
        for label in sorted(counts):
            hz = window_hz(label, now)
            if hz <= 0:
                continue
            seq_last, missing = (seqs.get(label) or (None, 0))[:2]
            gap = sorted(gaps[label])
            med = gap[len(gap) // 2] if gap else 0.0
            rows.append('  %-18s %5.1f Hz   序号 %-7s 丢 %-4d 帧间隔中位 %5.2f ms'
                        % (label, hz, seq_last if seq_last is not None else '-', missing, med))
        v_frames = voice['frames']
        v_samples = voice['samples']
        v_rate = voice['rate'] or 16000
        v_age = now - voice['last'] if voice['last'] else -1
        win = (voice['win_frames'], voice['win_samples'], voice['win_peak'],
               voice['win_energy'], voice['win_active'])
        voice['win_frames'] = voice['win_samples'] = voice['win_active'] = 0
        voice['win_peak'] = voice['win_energy'] = 0.0
        pcm_len = len(voice['pcm'])
        pcm_snapshot = bytes(voice['pcm'])
        conns = dict(connections)
        sessions = len(session_ids)
        sync_acks = sync['acks']
        rtt = sorted(sync['rtt_ms'])
    print('---- %s ----' % time.strftime('%H:%M:%S'), flush=True)
    print('  位姿连接 %d · 语音连接 %d · 会话 %d · 校时应答 %d%s'
          % (conns.get('pose', 0), conns.get('voice', 0), sessions, sync_acks,
             (' · RTT %.1f ms' % rtt[len(rtt) // 2]) if rtt else ''), flush=True)
    if rows:
        for row in rows:
            print(row, flush=True)
    else:
        print('  (%d 端口上没有解析出的位姿行)' % PORTS['pose'], flush=True)
    if v_frames:
        win_frames, win_samples, win_peak, win_energy, win_active = win
        seconds = v_samples / v_rate
        rms = np.sqrt(win_energy / win_samples) if win_samples else 0.0
        active = 100.0 * win_active / win_samples if win_samples else 0.0
        print('  语音  本秒 %2d 帧 · 峰值 %6.1f dBFS · RMS %6.1f dBFS · 有声样本 %4.1f%% '
              '· 累计 %d 帧 / %.1f s · 最后帧 %.1f s 前'
              % (win_frames, 20 * np.log10(win_peak) if win_peak > 0 else -120.0,
                 20 * np.log10(rms) if rms > 0 else -120.0, active,
                 v_frames, seconds, v_age), flush=True)
        if pcm_len:
            try:
                write_wav(WAV_PATH, pcm_snapshot, v_rate)
                print('        录音已存 → %s（%.1f s，可用 Tools\\transcribe_voice.ps1 转文字）'
                      % (WAV_PATH, pcm_len / 2.0 / v_rate), flush=True)
            except Exception as error:
                print('        [!] WAV 写入失败: %s' % error, flush=True)
    else:
        print('  语音  (%d 端口上没有 VoicePCM 帧)' % PORTS['voice'], flush=True)


# ---------------------------------------------------------------------------
# 可视化
# ---------------------------------------------------------------------------

BODY_BONES = [
    ('hips', 'spine-lower'), ('spine-lower', 'spine-middle'), ('spine-middle', 'spine-upper'),
    ('spine-upper', 'chest'), ('chest', 'neck'), ('neck', 'head'),
    ('chest', 'left-shoulder'), ('left-shoulder', 'left-scapula'), ('left-scapula', 'left-arm-upper'),
    ('left-arm-upper', 'left-arm-lower'), ('chest', 'right-shoulder'), ('right-shoulder', 'right-scapula'),
    ('right-scapula', 'right-arm-upper'), ('right-arm-upper', 'right-arm-lower'),
    ('hips', 'left-upper-leg'), ('left-upper-leg', 'left-lower-leg'), ('left-lower-leg', 'left-foot-ankle'),
    ('left-foot-ankle', 'left-foot-ball'), ('hips', 'right-upper-leg'), ('right-upper-leg', 'right-lower-leg'),
    ('right-lower-leg', 'right-foot-ankle'), ('right-foot-ankle', 'right-foot-ball'),
]
HAND_BONES = [(0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8),
              (0, 9), (9, 10), (10, 11), (11, 12), (0, 13), (13, 14), (14, 15), (15, 16),
              (0, 17), (17, 18), (18, 19), (19, 20)]


def quat_inv(q):
    return (-q[0], -q[1], -q[2], q[3])


def quat_rotate(q, v):
    xyz = np.array([q[0], q[1], q[2]])
    t = 2.0 * np.cross(xyz, v)
    return v + q[3] * t + np.cross(xyz, t)


def to_plot(p):
    """Unity(Y-up) -> matplotlib(Z-up)."""
    return np.array([p[0], p[2], p[1]])


def build_scene():
    """
    IOBT 激活时 App 已经把除 hips 以外的所有关节、头、手、手柄都转成 hips(躯干)系，
    hips 自身发世界系；所以画图只需把 hips 挪到原点，其余位置直接用，不能再旋转一次。
    没有 body 流时全部是世界系，同一套代码照画。
    """
    with lock:
        body = dict(state['body'])
        head = state['head']
        lh, rh = state['left_hand'], state['right_hand']
        lc, rc = state['left_ctrl'], state['right_ctrl']
    scene = {'body': {}, 'head': None, 'hands': {}, 'ctrls': {}}
    for name, (p, q) in body.items():
        scene['body'][name] = np.zeros(3) if name == 'hips' else to_plot(np.array(p))
    if head is not None:
        scene['head'] = to_plot(np.array(head[0]))
    for side, hand in (('left', lh), ('right', rh)):
        if hand is None:
            continue
        wp, wq, lm = np.array(hand[0]), np.array(hand[1]), hand[2]
        fingers = None
        if lm is not None:
            fingers = np.array([to_plot(quat_rotate(wq, np.array(p)) + wp) for p in lm])
        scene['hands'][side] = {'wrist': to_plot(wp), 'fingers': fingers}
    for side, ctrl in (('left', lc), ('right', rc)):
        if ctrl is not None:
            scene['ctrls'][side] = to_plot(np.array(ctrl[0]))
    return scene


def run_gui():
    import matplotlib
    matplotlib.use('TkAgg')
    import matplotlib.pyplot as plt
    from matplotlib.animation import FuncAnimation

    matplotlib.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'DejaVu Sans']
    matplotlib.rcParams['axes.unicode_minus'] = False

    fig = plt.figure(figsize=(11, 8))
    ax = fig.add_subplot(2, 1, 1, projection='3d')
    ax_wave = fig.add_subplot(2, 1, 2)
    fig.suptitle('MORLA XR live probe — 8000 pose / 8001 voice', fontsize=11)

    body_scatter = ax.scatter([], [], [], c='#ff8c00', s=40)
    body_lines = [ax.plot([], [], [], c='#ffb347', lw=2)[0] for _ in BODY_BONES]
    head_scatter = ax.scatter([], [], [], c='#00bfff', s=90)
    hand_scatter, hand_lines, ctrl_scatter = {}, {}, {}
    for side, color in (('left', '#00c853'), ('right', '#d500f9')):
        hand_scatter[side] = ax.scatter([], [], [], c=color, s=25)
        hand_lines[side] = [ax.plot([], [], [], c=color, lw=1.5)[0] for _ in HAND_BONES]
        ctrl_scatter[side] = ax.scatter([], [], [], c='#ff1744', s=120, marker='^')
    ax.set_xlabel('X right'); ax.set_ylabel('Z forward'); ax.set_zlabel('Y up')
    ax.set_box_aspect((1, 1, 1))
    status = fig.text(0.01, 0.005, '', fontsize=8, va='bottom')
    wave_line, = ax_wave.plot([], [], c='#00c853', lw=0.8)
    ax_wave.set_ylim(-1.05, 1.05); ax_wave.set_xlim(0, 32000)
    ax_wave.set_title('voice PCM16 16 kHz — 最近 2 s', fontsize=9)
    ax_wave.grid(alpha=0.2)

    def update(_frame):
        scene = build_scene()
        pts = list(scene['body'].values())
        if pts:
            arr = np.array(pts)
            body_scatter._offsets3d = (arr[:, 0], arr[:, 1], arr[:, 2])
            for line, (a, b) in zip(body_lines, BODY_BONES):
                if a in scene['body'] and b in scene['body']:
                    pa, pb = scene['body'][a], scene['body'][b]
                    line.set_data([pa[0], pb[0]], [pa[1], pb[1]])
                    line.set_3d_properties([pa[2], pb[2]])
                else:
                    line.set_data([], []); line.set_3d_properties([])
        else:
            body_scatter._offsets3d = ([], [], [])
        if scene['head'] is not None:
            head_scatter._offsets3d = ([scene['head'][0]], [scene['head'][1]], [scene['head'][2]])
        else:
            head_scatter._offsets3d = ([], [], [])
        for side in ('left', 'right'):
            hand = scene['hands'].get(side)
            if hand is None or hand['fingers'] is None:
                hand_scatter[side]._offsets3d = ([], [], [])
                for line in hand_lines[side]:
                    line.set_data([], []); line.set_3d_properties([])
                continue
            allp = np.vstack([hand['wrist'], hand['fingers']])
            hand_scatter[side]._offsets3d = (allp[:, 0], allp[:, 1], allp[:, 2])
            for line, (a, b) in zip(hand_lines[side], HAND_BONES):
                line.set_data([allp[a][0], allp[b][0]], [allp[a][1], allp[b][1]])
                line.set_3d_properties([allp[a][2], allp[b][2]])
            ctrl = scene['ctrls'].get(side)
            ctrl_scatter[side]._offsets3d = ([ctrl[0]], [ctrl[1]], [ctrl[2]]) if ctrl is not None else ([], [], [])
        span = []
        span.extend(scene['body'].values())
        if scene['head'] is not None:
            span.append(scene['head'])
        for hand in scene['hands'].values():
            span.append(hand['wrist'])
            if hand['fingers'] is not None:
                span.extend(hand['fingers'])
        span.extend(scene['ctrls'].values())
        if span:
            arr = np.array(span)
            center = arr.mean(axis=0)
            r = max(float(np.max(arr - center)), 0.4) * 1.1
            ax.set_xlim(center[0] - r, center[0] + r)
            ax.set_ylim(center[1] - r, center[1] + r)
            ax.set_zlim(center[2] - r, center[2] + r)
        with lock:
            chunks = list(voice['chunks'])
            v_frames, v_samples = voice['frames'], voice['samples']
            v_rms = voice['rms']
        if chunks:
            audio = np.concatenate(chunks)
            step = max(1, audio.size // 2000)
            audio = audio[::step]
            wave_line.set_data(np.arange(audio.size) * step, audio)
            ax_wave.set_xlim(0, max(1, audio.size * step))
        else:
            wave_line.set_data([], [])
        with lock:
            pose_conns = connections['pose']
        status.set_text('位姿连接 %d · 语音帧 %d / %.1f s · RMS %s dBFS'
                        % (pose_conns, v_frames, v_samples / 16000.0,
                           ('%.1f' % (20 * np.log10(v_rms))) if v_rms > 0 else '-'))
        return []

    anim = FuncAnimation(fig, update, interval=50, blit=False, cache_frame_data=False)
    fig._morla_anim = anim                     # 防止被 GC 后动画停摆
    plt.show()


def reporter():
    while True:
        time.sleep(1.0)
        try:
            report()
        except Exception as error:
            print('[!] 统计异常: %s' % error, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--pose-port', type=int, default=8000)
    parser.add_argument('--voice-port', type=int, default=8001)
    parser.add_argument('--host', default='127.0.0.1', help='监听地址；无线直连时用 0.0.0.0')
    parser.add_argument('--no-gui', action='store_true')
    parser.add_argument('--seconds', type=float, default=0, help='运行多少秒后退出，0 表示一直跑')
    parser.add_argument('--no-sync', action='store_true', help='不主动发 @q3sync 挑战')
    args = parser.parse_args()
    global BIND_HOST
    PORTS['pose'], PORTS['voice'] = args.pose_port, args.voice_port
    BIND_HOST = args.host

    threading.Thread(target=serve, args=(args.pose_port, handle_pose_line, 'pose', not args.no_sync),
                     daemon=True).start()
    threading.Thread(target=serve, args=(args.voice_port, handle_voice_line, 'voice', not args.no_sync),
                     daemon=True).start()

    started = time.time()
    threading.Thread(target=reporter, daemon=True).start()

    if args.no_gui:
        deadline = started + args.seconds if args.seconds > 0 else None
        while deadline is None or time.time() < deadline:
            time.sleep(0.5)
        return
    if args.seconds > 0:
        threading.Timer(args.seconds, os._exit, [0]).start()
    try:
        run_gui()
    except Exception as error:
        print('[!] GUI 不可用（%s），改为仅控制台' % error, flush=True)
        while True:
            time.sleep(1.0)


if __name__ == '__main__':
    main()
