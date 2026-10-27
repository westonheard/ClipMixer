# ClipMixer

An app I created to go through many clips fast, to cut down and replace them only if the export it does

## What it does
- Plays clips w/ mpv so scrubbing is smooth
- trim to in/out, lossless by default (starts on nearest keyframe) or frame accurate if you turn on precise cut
- checks the trimmed file actually plays before it replaces the original, original goes to recycle bin
- Per track audio effects: volume, mono/stereo, keep or drop tracks, mix to one track
- effects on each track (compressor, noise gate, voice isolation, bitcrusher, etc)
- waveforms for each audio track under the timeline
- remembers which clips you already trimmed

## Setup
needs windows + .NET 8 SDK

ffmpeg and libmpv are too big for github so run this first, it downloads them into the right folders:

```
powershell -ExecutionPolicy Bypass -File setup.ps1
```

then open ClipMixer.sln and build/run (x64 only)

if u wanna do it by hand:
- ffmpeg.exe + ffprobe.exe from https://www.gyan.dev/ffmpeg/builds/ go in `ClipMixer/tools/`
- libmpv from https://github.com/shinchiro/mpv-winbuild-cmake/releases (the mpv-dev-x86_64 one), rename `libmpv-2.dll` to `mpv-1.dll` and put it in `ClipMixer/mpv/`

## keybinds
| key | does |
|---|---|
| space | play/pause |
| i / o | set in / out |
| left / right | seek 5s (shift = 1s) |
| , . | frame step |
| home | jump to in |
| up/down or p/n | prev/next clip |
| enter | trim + replace |
| del | delete clip |
| ctrl+o | open folder |
