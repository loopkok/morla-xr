#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Quest3 MORLA XR —— 骨架实时可视化（TCP 有线）

接收 Quest 3 App 推流的身体(IOBT) / 头 / 手腕 / 手指 / 手柄数据，
用 matplotlib 3D 实时画出人体骨架。

用法：
  1. 安装依赖:   pip install numpy matplotlib
  2. ADB 反向端口(Quest 是客户端、PC 是服务端):  adb reverse tcp:8000 tcp:8000
  3. 运行本脚本:  python skeleton_viewer.py
  4. Quest 上选「TCP 有线(127.0.0.1:8000)」，勾选 IOBT，点开始推流

坐标系说明：
  - IOBT 开启时，手/头/手柄数据已经是「身体(hips)相对坐标」。
  - 身体关节本身是世界坐标，本脚本会统一变换到 hips 坐标系，使 hips 为原点。
  - 手指 landmark 是手腕局部坐标，本脚本会乘手腕位姿还原到 hips 系。
"""

import socket
import threading
import re
import numpy as np
import matplotlib
matplotlib.use('TkAgg')
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d import Axes3D  # noqa: F401
from matplotlib.animation import FuncAnimation

HOST = '127.0.0.1'
PORT = 8000

# ---------------------------------------------------------------------------
# 骨骼连接定义（parent -> child）
# ---------------------------------------------------------------------------

# 身体（IOBT FullBody，31 关节里的连接关系）
BODY_BONES = [
    ('hips', 'spine-lower'),
    ('spine-lower', 'spine-middle'),
    ('spine-middle', 'spine-upper'),
    ('spine-upper', 'chest'),
    ('chest', 'neck'),
    ('neck', 'head'),
    ('chest', 'left-shoulder'),
    ('left-shoulder', 'left-scapula'),
    ('left-scapula', 'left-arm-upper'),
    ('left-arm-upper', 'left-arm-lower'),
    ('chest', 'right-shoulder'),
    ('right-shoulder', 'right-scapula'),
    ('right-scapula', 'right-arm-upper'),
    ('right-arm-upper', 'right-arm-lower'),
    ('hips', 'left-upper-leg'),
    ('left-upper-leg', 'left-lower-leg'),
    ('left-lower-leg', 'left-foot-ankle'),
    ('left-foot-ankle', 'left-foot-ball'),
    ('hips', 'right-upper-leg'),
    ('right-upper-leg', 'right-lower-leg'),
    ('right-lower-leg', 'right-foot-ankle'),
    ('right-foot-ankle', 'right-foot-ball'),
]

# 手部 21 关键点（streamed 顺序）的骨骼连接
# 索引：0=腕, 1-4=拇指, 5-8=食指, 9-12=中指, 13-16=无名指, 17-20=小指
HAND_BONES = [
    (0, 1), (1, 2), (2, 3), (3, 4),        # 拇指
    (0, 5), (5, 6), (6, 7), (7, 8),        # 食指
    (0, 9), (9, 10), (10, 11), (11, 12),   # 中指
    (0, 13), (13, 14), (14, 15), (15, 16), # 无名指
    (0, 17), (17, 18), (18, 19), (19, 20), # 小指
]

# ---------------------------------------------------------------------------
# 四元数工具（Unity 约定 x,y,z,w）
# ---------------------------------------------------------------------------

def quat_inv(q):
    """单位四元数的逆 = 共轭。"""
    return (-q[0], -q[1], -q[2], q[3])


def quat_rotate(q, v):
    """用单位四元数 q=(x,y,z,w) 旋转向量 v。"""
    qx, qy, qz, qw = q[0], q[1], q[2], q[3]
    xyz = np.array([qx, qy, qz])
    t = 2.0 * np.cross(xyz, v)
    return v + qw * t + np.cross(xyz, t)


def to_plot(p):
    """Unity 坐标(Y-up) -> matplotlib 坐标(Z-up)。"""
    return np.array([p[0], p[2], p[1]])


# ---------------------------------------------------------------------------
# 共享状态（接收线程写，绘图线程读）
# ---------------------------------------------------------------------------

state = {
    'body': {},      # joint name -> (pos, quat)  世界坐标
    'hips': None,    # (pos, quat) 世界坐标
    'head': None,    # (pos, quat) body 相对
    'left_hand': None,   # (wrist_pos, wrist_quat, [21 landmarks 局部])
    'right_hand': None,
    'left_ctrl': None,
    'right_ctrl': None,
}
lock = threading.Lock()

FLOAT_RE = re.compile(r'[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?')


def extract_floats(text):
    return [float(x) for x in FLOAT_RE.findall(text)]


def parse_line(line):
    """解析一行数据，返回 (kind, payload)。无法识别返回 None。"""
    line = line.strip()
    if not line:
        return None

    # 身体： body iobt | fid=High: hips,x,y,z,qx,qy,qz,qw spine-lower,...
    if line.startswith('body iobt'):
        prefix, _, rest = line.partition(':')
        joints = {}
        for tok in rest.split():
            parts = tok.split(',')
            if len(parts) != 8:
                continue
            name = parts[0]
            vals = [float(v) for v in parts[1:]]
            joints[name] = (np.array(vals[0:3]), np.array(vals[3:7]))
        return ('body', joints)

    # 手/头/手柄： Label: x, y, z, qx, qy, qz, qw
    label, _, rest = line.partition(':')
    label = label.strip()
    if label in ('Left wrist', 'Right wrist', 'Left controller',
                 'Right controller', 'Head pose'):
        vals = extract_floats(rest)
        if len(vals) >= 7:
            return ('pose', label, np.array(vals[0:3]), np.array(vals[3:7]))
        return None

    # 手指 landmark： Left landmarks: x,y,z, x,y,z, ... (63 个)
    if label in ('Left landmarks', 'Right landmarks'):
        vals = extract_floats(rest)
        if len(vals) >= 63:
            pts = np.array(vals[0:63]).reshape(21, 3)
            return ('landmarks', label, pts)
        return None

    return None


def update_state(kind, payload):
    with lock:
        if kind == 'body':
            joints = payload
            if 'hips' in joints:
                state['body'] = joints
                state['hips'] = joints['hips']
        elif kind == 'pose':
            _, label, pos, quat = payload
            if label == 'Head pose':
                state['head'] = (pos, quat)
            elif label == 'Left controller':
                state['left_ctrl'] = (pos, quat)
            elif label == 'Right controller':
                state['right_ctrl'] = (pos, quat)
            elif label == 'Left wrist':
                if state['left_hand'] is None:
                    state['left_hand'] = (pos, quat, None)
                else:
                    _, _, lm = state['left_hand']
                    state['left_hand'] = (pos, quat, lm)
            elif label == 'Right wrist':
                if state['right_hand'] is None:
                    state['right_hand'] = (pos, quat, None)
                else:
                    _, _, lm = state['right_hand']
                    state['right_hand'] = (pos, quat, lm)
        elif kind == 'landmarks':
            _, label, pts = payload
            if label == 'Left landmarks':
                if state['left_hand'] is None:
                    state['left_hand'] = (np.zeros(3), np.array([0, 0, 0, 1]), pts)
                else:
                    wp, wq, _ = state['left_hand']
                    state['left_hand'] = (wp, wq, pts)
            elif label == 'Right landmarks':
                if state['right_hand'] is None:
                    state['right_hand'] = (np.zeros(3), np.array([0, 0, 0, 1]), pts)
                else:
                    wp, wq, _ = state['right_hand']
                    state['right_hand'] = (wp, wq, pts)


def receiver_loop():
    """后台线程：接收 TCP 数据并解析。"""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((HOST, PORT))
    sock.listen(1)
    print(f'[*] 监听 {HOST}:{PORT}，等待 Quest 连接...')
    buf = b''
    while True:
        try:
            conn, addr = sock.accept()
            print(f'[+] Quest 已连接: {addr}')
            with conn:
                while True:
                    data = conn.recv(65536)
                    if not data:
                        break
                    buf += data
                    while b'\n' in buf:
                        line, buf = buf.split(b'\n', 1)
                        try:
                            r = parse_line(line.decode('utf-8', 'replace'))
                            if r is not None:
                                update_state(r[0], r[1:])
                        except Exception as e:
                            print(f'[!] 解析错误: {e}')
            print('[-] Quest 断开，等待重连...')
        except Exception as e:
            print(f'[!] 接收异常: {e}')
            threading.Event().wait(1.0)


# ---------------------------------------------------------------------------
# 绘图
# ---------------------------------------------------------------------------

def build_scene():
    """从共享状态构建 hips 坐标系下的绘图数据。返回 dict。"""
    with lock:
        hips = state['hips']
        body = state['body']
        head = state['head']
        lh = state['left_hand']
        rh = state['right_hand']
        lc = state['left_ctrl']
        rc = state['right_ctrl']

    scene = {'body_pts': {}, 'head': None, 'hands': {}, 'ctrls': {}}

    if hips is None:
        return scene

    hips_pos, hips_quat = hips
    inv_q = quat_inv(hips_quat)

    # 身体关节：世界 -> hips 系
    for name, (p, q) in body.items():
        rel = quat_rotate(inv_q, np.array(p) - np.array(hips_pos))
        scene['body_pts'][name] = to_plot(rel)

    # 头：已经是 body 相对
    if head is not None:
        scene['head'] = to_plot(head[0])

    # 手：手腕 body 相对；landmark 是手腕局部，乘手腕位姿还原到 hips 系
    def build_hand(hand):
        if hand is None:
            return None
        wp, wq, lm = hand
        wrist = to_plot(wp)
        if lm is None:
            return {'wrist': wrist, 'fingers': None}
        finger_pts = []
        for local in lm:
            world_local = quat_rotate(wq, np.array(local)) + np.array(wp)
            finger_pts.append(to_plot(world_local))
        return {'wrist': wrist, 'fingers': np.array(finger_pts)}

    if lh is not None:
        scene['hands']['left'] = build_hand(lh)
    if rh is not None:
        scene['hands']['right'] = build_hand(rh)

    # 手柄：body 相对
    if lc is not None:
        scene['ctrls']['left'] = to_plot(lc[0])
    if rc is not None:
        scene['ctrls']['right'] = to_plot(rc[0])

    return scene


fig = plt.figure(figsize=(9, 9))
ax = fig.add_subplot(111, projection='3d')

body_scatter = ax.scatter([], [], [], c='#ff8c00', s=40, label='body')
body_lines = [ax.plot([], [], [], c='#ffb347', lw=2)[0] for _ in BODY_BONES]
head_scatter = ax.scatter([], [], [], c='#00bfff', s=80, marker='o', label='head')

hand_scatters = {}
hand_lines = {}
for side, color in (('left', '#00c853'), ('right', '#d500f9')):
    hand_scatters[side] = ax.scatter([], [], [], c=color, s=25)
    hand_lines[side] = [ax.plot([], [], [], c=color, lw=1.5)[0]
                        for _ in HAND_BONES]

ctrl_scatters = {}
for side, color in (('left', '#ff1744'), ('right', '#ff1744')):
    ctrl_scatters[side] = ax.scatter([], [], [], c=color, s=120, marker='^')

ax.set_xlabel('X (right)')
ax.set_ylabel('Z (forward)')
ax.set_zlabel('Y (up)')
ax.set_title('MORLA XR Skeleton (IOBT / body frame)')
ax.legend(loc='upper left')


def update_plot(frame):
    scene = build_scene()

    # 身体
    bp = scene['body_pts']
    if bp:
        names = list(bp.keys())
        pts = np.array([bp[n] for n in names])
        body_scatter._offsets3d = (pts[:, 0], pts[:, 1], pts[:, 2])
        for line, (pa, pb) in zip(body_lines, BODY_BONES):
            if pa in bp and pb in bp:
                line.set_data([bp[pa][0], bp[pb][0]],
                              [bp[pa][1], bp[pb][1]])
                line.set_3d_properties([bp[pa][2], bp[pb][2]])
            else:
                line.set_data([], [])
                line.set_3d_properties([])
    else:
        body_scatter._offsets3d = ([], [], [])

    # 头
    if scene['head'] is not None:
        h = scene['head']
        head_scatter._offsets3d = ([h[0]], [h[1]], [h[2]])
    else:
        head_scatter._offsets3d = ([], [], [])

    # 手
    for side in ('left', 'right'):
        hs = hand_scatters[side]
        hl = hand_lines[side]
        hand = scene['hands'].get(side)
        if hand is None or hand['fingers'] is None:
            hs._offsets3d = ([], [], [])
            for l in hl:
                l.set_data([], [])
                l.set_3d_properties([])
            continue
        fp = hand['fingers']
        all_pts = np.vstack([hand['wrist'], fp])
        hs._offsets3d = (all_pts[:, 0], all_pts[:, 1], all_pts[:, 2])
        for l, (a, b) in zip(hl, HAND_BONES):
            l.set_data([all_pts[a][0], all_pts[b][0]],
                       [all_pts[a][1], all_pts[b][1]])
            l.set_3d_properties([all_pts[a][2], all_pts[b][2]])

    # 手柄
    for side in ('left', 'right'):
        cs = ctrl_scatters[side]
        c = scene['ctrls'].get(side)
        if c is not None:
            cs._offsets3d = ([c[0]], [c[1]], [c[2]])
        else:
            cs._offsets3d = ([], [], [])

    # 自动缩放范围
    all_p = []
    if scene['body_pts']:
        all_p.extend(scene['body_pts'].values())
    if scene['head'] is not None:
        all_p.append(scene['head'])
    for side in ('left', 'right'):
        hand = scene['hands'].get(side)
        if hand is not None and hand['fingers'] is not None:
            all_p.append(hand['wrist'])
            all_p.extend(hand['fingers'])
    if all_p:
        arr = np.array(all_p)
        center = arr.mean(axis=0)
        r = max(np.max(arr[:, 0] - center[0]), np.max(arr[:, 1] - center[1]),
                np.max(arr[:, 2] - center[2]), 0.3)
        ax.set_xlim(center[0] - r, center[0] + r)
        ax.set_ylim(center[1] - r, center[1] + r)
        ax.set_zlim(center[2] - r, center[2] + r)

    return []


if __name__ == '__main__':
    threading.Thread(target=receiver_loop, daemon=True).start()
    ani = FuncAnimation(fig, update_plot, interval=33, blit=False, cache_frame_data=False)
    print('[*] 开始可视化（按 Ctrl+C 退出，或关闭窗口）')
    plt.show()
