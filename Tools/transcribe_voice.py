#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 morla_live_probe.py 录下的 voice_capture.wav 转成文字（faster-whisper，CPU int8）。

这只是一个**验收工具**：工程本身不含语音识别，正式的"语音转文字"在下游主机侧。
用它可以回答"8001 上收到的到底是人声还是静音/噪声"。

准备（一次性，国内走 ModelScope 镜像；HuggingFace 通常不可达）：
    pip install faster-whisper -i https://pypi.tuna.tsinghua.edu.cn/simple
    python Tools/transcribe_voice.py --download      # 从 ModelScope 拉 small 模型到 ~/.cache/morla-asr

用法：
    python Tools/transcribe_voice.py                          # 默认转 Tools/voice_capture.wav
    python Tools/transcribe_voice.py some.wav --language zh
    python Tools/transcribe_voice.py --download --model small

注意：本机 numpy/onnxruntime 各带一份 OpenMP，必须在导入 ctranslate2 之前设
KMP_DUPLICATE_LIB_OK，否则直接崩（见脚本顶部）。
"""

import argparse
import os
import sys
import wave

os.environ.setdefault('KMP_DUPLICATE_LIB_OK', 'TRUE')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

CACHE = os.path.join(os.path.expanduser('~'), '.cache', 'morla-asr')
MIRROR = 'https://modelscope.cn/models/Systran/faster-whisper-%s/resolve/master/'
FILES = ('config.json', 'tokenizer.json', 'vocabulary.txt', 'model.bin')


def download(model):
    import time
    import urllib.request
    dest = os.path.join(CACHE, 'faster-whisper-' + model)
    os.makedirs(dest, exist_ok=True)
    for name in FILES:
        path = os.path.join(dest, name)
        if os.path.exists(path) and os.path.getsize(path) > 0:
            continue
        started = time.time()
        urllib.request.urlretrieve(MIRROR % model + name, path + '.part')
        os.replace(path + '.part', path)
        print('  %-16s %7.1f MB  %3.0fs' % (name, os.path.getsize(path) / 1e6, time.time() - started))
    print('模型目录：' + dest)
    return dest


def read_wav(path):
    import numpy as np
    with wave.open(path) as w:
        rate = w.getframerate()
        channels = w.getnchannels()
        audio = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float32) / 32768.0
    if channels != 1:
        audio = audio.reshape(-1, channels).mean(axis=1)
    if rate != 16000:
        print('[!] 采样率 %d Hz（faster-whisper 内部会按 16 kHz 处理，建议先重采样）' % rate)
    return audio


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('wav', nargs='?', default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                               'voice_capture.wav'))
    parser.add_argument('--model', default='small', help='tiny / base / small / medium')
    parser.add_argument('--model-dir', default=None, help='直接指定本地模型目录')
    parser.add_argument('--language', default='zh')
    parser.add_argument('--download', action='store_true', help='只下载模型')
    args = parser.parse_args()

    model_dir = args.model_dir or os.path.join(CACHE, 'faster-whisper-' + args.model)
    if args.download:
        download(args.model)
        return
    if not os.path.isdir(model_dir):
        print('找不到模型：%s\n先运行： python Tools/transcribe_voice.py --download --model %s'
              % (model_dir, args.model))
        sys.exit(1)
    if not os.path.exists(args.wav):
        print('找不到录音：' + args.wav)
        sys.exit(1)

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        print('未安装 faster-whisper：pip install faster-whisper -i https://pypi.tuna.tsinghua.edu.cn/simple')
        sys.exit(1)

    audio = read_wav(args.wav)
    model = WhisperModel(model_dir, device='cpu', compute_type='int8')
    segments, info = model.transcribe(audio, language=args.language, vad_filter=True,
                                      vad_parameters=dict(min_silence_duration_ms=400), beam_size=5)
    print('时长 %.1fs · 语言 %s (p=%.2f) · 模型 %s' % (info.duration, info.language,
                                                  info.language_probability, args.model))
    count = 0
    for segment in segments:
        count += 1
        print('[%6.2f-%6.2f] %s' % (segment.start, segment.end, segment.text.strip()))
    if count == 0:
        print('没有识别出语音：这一段可能是静音，或说话声太小。')


if __name__ == '__main__':
    main()
