# Third-party notices — IPMsg Phone Dock

IPMsg Phone Dock is an independent desktop companion. It is not endorsed by
IP Messenger, FastCopy Lab, Genymobile, Google, SDL, FFmpeg or the other upstream
projects. The dock's own C# source is MIT licensed. Bundled runtime components
retain their separate licenses; the complete bundle is not licensed solely under
MIT or Apache 2.0.

## Runtime provenance

The runtime files in `runtime/scrcpy/` were obtained from the official,
unmodified **scrcpy 5.0.1 Windows x64** distribution:

- Archive: https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip
- Archive SHA-256: `b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5`
- Original upstream license: `runtime/scrcpy/LICENSE.txt` and `licenses/Apache-2.0.txt`
- Source, build instructions and provenance limits: `licenses/CORRESPONDING-SOURCE.md`

Dependency versions come from the upstream v5.0.1 release/dependency scripts and,
where available, matching DLL metadata or embedded strings. Original notices and
matching source archives are included in `licenses/`. **Keep this directory,
including `licenses/sources/`, with redistributed binary packages.**

| Component / files | Version | License / supplied material |
|---|---|---|
| scrcpy client, Android server, supporting artwork and scripts | 5.0.1 | Apache 2.0; complete source archive and license supplied |
| `avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` | FFmpeg 9.0.2 | LGPL 2.1 or later; `FFmpeg-9.0.2-COPYING.LGPLv2.1.txt`, upstream LICENSE and complete corresponding source archive supplied |
| dav1d, statically included in FFmpeg | 1.5.4 | BSD 2-Clause; `dav1d-1.5.4-COPYING.txt`, complete source archive supplied |
| zlib, statically included in FFmpeg | 1.3.1 | zlib license; `zlib-1.3.1-LICENSE.txt`, source archive supplied |
| `SDL3.dll` | 3.4.18 | zlib license; `SDL-3.4.18-LICENSE.txt` supplied; pinned source link in source guide |
| `libusb-1.0.dll` | 1.0.30 | LGPL 2.1 or later; `libusb-1.0.30-COPYING.txt`, complete corresponding source archive supplied |
| `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | Google platform-tools 37.0.1 | AOSP/third-party terms, including Apache 2.0, BSD, MIT and other component licenses; complete original `ADB-37.0.1-NOTICE.txt` supplied |
| MinGW/GCC runtime support in native binaries | GCC 13 identified; exact distribution patch level not pinned | GCC Runtime Library Exception 3.1 with GPLv3 text, and MinGW-w64 COPYING included as applicable; see provenance limits |

### FFmpeg and libusb rights

This software uses libraries from the FFmpeg project under the LGPL version 2.1
or later, and libusb under the LGPL version 2.1 or later. These libraries remain
separate dynamically linked DLLs used by the scrcpy executable. You may modify
and rebuild them and replace them with compatible versions. The dock imposes no
restriction on reverse engineering for debugging such modifications. See
`licenses/CORRESPONDING-SOURCE.md` for the included corresponding sources and
upstream build instructions. The presence of GPL texts in the upstream FFmpeg
source archive does not license the dock's own code under the GPL. The shipped
FFmpeg DLL identifies itself as LGPL 2.1 or later, and its embedded configuration
does not enable GPL or nonfree components.

This product includes software developed by the FFmpeg project. It also includes
software developed by the VideoLAN and dav1d authors and by the Independent JPEG
Group, where applicable to the bundled FFmpeg sources. No upstream component was
modified for this bundle.

### ADB notices

`licenses/ADB-37.0.1-NOTICE.txt` is copied in full from Google's exact
platform-tools 37.0.1 Windows ZIP, not reconstructed from an assumed Apache-only
license. The ZIP SHA-256 is
`45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99`.
The bundled ADB components have the sizes recorded in that archive. AOSP source
links are provided in the source guide; the exact Google binary source commit is
not identified by scrcpy's dependency script.

## Original IP Messenger icon

`assets/ipmsg.ico` is the original icon from
https://github.com/shirouzu/ipmsg/blob/master/src/res/ipmsg.ico.

Icon SHA-256:
`bf75115404061b7a8059f8a8c1bcab099e8e412a7535a44566af80df693f1bd8`

The original BSD notice is reproduced below and in
`licenses/IP-Messenger-BSD.txt`, from
https://github.com/shirouzu/ipmsg/blob/master/readme.txt:

```
Copyright (c) 1996-2019 SHIROUZU Hiroaki All rights reserved.
  Copyright (c) 2015-2018 Asahi Net, Inc. All Rights Reserved.
  Copyright (c) 2018-2019 FastCopy Lab, LLC. All Rights Reserved.

  Redistribution and use in source and binary forms, with or
  without modification, are permitted provided that the following
  conditions are met:

  1. Redistributions of source code must retain the above
     copyright notice, this list of conditions and the following
     disclaimer.

  2. Redistributions in binary form must reproduce the above
     copyright notice, this list of conditions and the following
     disclaimer in the documentation and/or other materials
     provided with the distribution.

  THIS SOFTWARE IS PROVIDED BY SHIROUZU Hiroaki, Asahi Net, Inc.,
  and FastCopy Lab, LLC. ``AS IS'' AND ANY EXPRESS OR
  IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
  WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
  ARE DISCLAIMED.
  IN NO EVENT SHALL SHIROUZU Hiroaki OR CONTRIBUTORS BE LIABLE
  FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
  CONSEQUENTIAL DAMAGES(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT
  OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS;
  OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
  LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
  (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
  THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY
  OF SUCH DAMAGE.
```

## Local QR encoder and icon pack

QRCoder 1.4.3 (MIT), obtained from the official NuGet package. The net40 QRCoder.dll is bundled locally; SHA-256 561acfe4b1a14c837b189fb9fc5c6d3e82440184bbde61912de723d62d6368b3. Complete upstream source is licenses/sources/QRCoder-1.4.3.zip and the full license is licenses/QRCoder-LICENSE.txt. Source: https://github.com/codebude/QRCoder/tree/v1.4.3 .

Lucide static 0.468.0, obtained from the official lucide-static npm package. The font is embedded in the executable; selected SVG originals, CSS mapping and the font are retained under assets. Full license/attribution (including the Feather-derived icon terms) is licenses/Lucide-LICENSE.txt; complete pinned package is licenses/sources/lucide-static-0.468.0.tgz. Source: https://github.com/lucide-icons/lucide . Neither library downloads resources at runtime. Preserve both notices and upstream source packages when redistributing.
