# ClipMixer

little app I made for going thru a folder of game clips fast. pick a folder, watch each clip, set in/out, hit enter and it trims + replaces the original. or hit del and its gone

## what it does
- plays clips w/ mpv so scrubbing is actually smooth
- trim to in/out, lossless by default (starts on nearest keyframe) or frame accurate if u turn on precise cut
- checks the trimmed file actually plays before it replaces the original, original goes to recycle bin
- per track audio stuff: volume, mono/stereo, keep or drop tracks, mix to one track
- effects on each track (compressor, noise gate, voice isolation, bitcrusher, etc)
- waveforms for each audio track under the timeline
- remembers which clips u already trimmed

## setup
needs windows + .NET 8 SDK

ffmpeg and libmpv are too big for github so run this first, it downloads them into the right folders:

```
powershell -ExecutionPolicy Bypass -File setup.ps1
```

then open ClipMixer.sln and build/run (x64 only)

if u wanna do it by hand:
- ffmpeg.exe + ffprobe.exe from https://www.gyan.dev/ffmpeg/builds/ go in `ClipMixer/tools/`
- libmpv from https://github.com/shinchiro/mpv-winbuild-cmake/releases (the mpv-dev-x86_64 one), rename `libmpv-2.dll` to `mpv-1.dll` and put it in `ClipMixer/mpv/`

## keys
| key | does |
|---|---|
| space | play/pause |
| i / o | set in / out |
| left / right | seek 5s (shift = 1s) |
| home | jump to in |
| up/down or p/n | prev/next clip |
| enter | trim + replace |
| del | delete clip |
| ctrl+o | open folder |
