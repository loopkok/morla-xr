# 把 morla_live_probe.py 录下的 voice_capture.wav 转成文字（Windows 自带 SAPI 识别器，离线）。
# 用法： powershell -ExecutionPolicy Bypass -File Tools\transcribe_voice.ps1 [wav路径] [语言，如 zh-CN]
# 说明：SAPI 的桌面听写模型只适合验证"语音链路里有没有人声"，不是高精度转写；
#       正式接下游请用工程外的语音识别服务（本项目自身不含 STT）。

param(
    [string]$Wav = (Join-Path $PSScriptRoot 'voice_capture.wav'),
    [string]$Culture = 'zh-CN'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Wav)) { Write-Host "找不到录音：$Wav" -ForegroundColor Red; exit 1 }
Add-Type -AssemblyName System.Speech

function New-Engine([string]$name) {
    if ($name) {
        try {
            return New-Object System.Speech.Recognition.SpeechRecognitionEngine(
                (New-Object System.Globalization.CultureInfo($name)))
        } catch { Write-Host "没有 $name 识别器，改用系统默认" -ForegroundColor Yellow }
    }
    return New-Object System.Speech.Recognition.SpeechRecognitionEngine
}

$engine = New-Engine $Culture
Write-Host ("识别器：{0}" -f $engine.RecognizerInfo.Culture.Name)
$engine.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))
$engine.SetInputToWaveFile($Wav)
$engine.InitialSilenceTimeout = [TimeSpan]::FromSeconds(2)
$engine.BabbleTimeout = [TimeSpan]::FromSeconds(2)
$engine.EndSilenceTimeout = [TimeSpan]::FromSeconds(0.5)

$found = 0
while ($true) {
    try { $result = $engine.Recognize() } catch { break }
    if ($null -eq $result) { break }
    $found++
    Write-Host ("[{0,5:N2}s] {1}  (置信度 {2:P0})" -f $result.Audio.AudioPosition.TotalSeconds, $result.Text, $result.Confidence)
}
$engine.Dispose()
if ($found -eq 0) {
    Write-Host '没有识别出任何语句：可能整段是静音，或识别器不匹配语言。' -ForegroundColor Yellow
}
