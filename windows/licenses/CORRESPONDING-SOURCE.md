# Bundled runtime sources and rebuilding

The Phone Dock's own C# code is MIT licensed. Its separate runtime components
have their own licenses. Do not describe the entire portable bundle as MIT or
Apache licensed. The library replacement and reverse-engineering rights granted
by the LGPL are not restricted by the dock.

## Matching sources included with the binaries

Keep this directory, including `sources/`, with every redistributed portable
binary archive. These archives contain the upstream sources without local
modifications:

| Component | Source archive | Why included |
|---|---|---|
| scrcpy 5.0.1 | `sources/scrcpy-5.0.1.tar.gz` | Client/server sources, complete dependency and release build scripts |
| FFmpeg 9.0.2 | `sources/ffmpeg-9.0.2.tar.xz` | Corresponding sources for avcodec-63, avformat-63, avutil-61, swresample-7 DLLs; LGPL 2.1 or later |
| libusb 1.0.30 | `sources/libusb-1.0.30.tar.gz` | Corresponding sources for libusb-1.0.dll; LGPL 2.1 or later |
| dav1d 1.5.4 | `sources/dav1d-1.5.4.tar.xz` | BSD-licensed AV1 decoder statically included in FFmpeg |
| zlib 1.3.1 | `sources/zlib-1.3.1.tar.gz` | zlib-licensed dependency identified in FFmpeg DLL strings |

The FFmpeg, libusb and dav1d archive SHA-256 values were checked against the
values pinned by scrcpy's v5.0.1 `app/deps/*.sh` scripts. `SOURCE_PROVENANCE.json`
records download URLs and hashes; `SOURCE-SHA256SUMS.txt` records included file
hashes. Copies of the relevant upstream scripts are in `upstream-build/`.
The complete scrcpy source archive also contains those scripts in their original
directory layout.

## Build instructions

Use the scrcpy 5.0.1 source archive and its upstream release workflow:

- [Windows build script](https://github.com/Genymobile/scrcpy/blob/v5.0.1/release/build_windows.sh)
- [Pinned dependency scripts](https://github.com/Genymobile/scrcpy/tree/v5.0.1/app/deps)
- [Release environment and build commands](https://github.com/Genymobile/scrcpy/blob/v5.0.1/.github/workflows/release.yml)

The official win64 build is cross-compiled from Linux with MinGW-w64. The
workflow installs Meson, Ninja, NASM, MinGW-w64 and `libz-mingw-w64-dev`.
The individual dependency builds also require their normal tools: a C compiler,
Make, pkg-config, CMake, Autoconf, Automake, Libtool, tar, wget and a SHA-256 tool.

For example, from the portable bundle directory, in an appropriate Linux build
environment:

```sh
mkdir -p rebuild
tar -xzf licenses/sources/scrcpy-5.0.1.tar.gz -C rebuild
cd rebuild/scrcpy-5.0.1
mkdir -p app/deps/work/sources
cp ../../licenses/sources/ffmpeg-9.0.2.tar.xz app/deps/work/sources/
cp ../../licenses/sources/dav1d-1.5.4.tar.xz app/deps/work/sources/
cp ../../licenses/sources/libusb-1.0.30.tar.gz app/deps/work/sources/
app/deps/dav1d.sh win64 cross shared
app/deps/ffmpeg.sh win64 cross shared
app/deps/libusb.sh win64 cross shared
```

The scripts preserve the published configure flags and use the included archive
files instead of downloading them again. See the workflow and scripts for a full
scrcpy build, including SDL and ADB. `release/build_windows.sh 64` builds the
Windows client and dependencies; server/package steps are documented by the
remaining upstream release scripts.

After rebuilding compatible DLLs, close the dock, back up the relevant DLLs in
`runtime/scrcpy/`, and replace them with your builds. Keep the expected ABI DLL
names and architecture. No signature check or other dock restriction prevents
replacing these libraries. The bundled scrcpy client is dynamically linked to
FFmpeg and libusb; no relinking of the C# dock is needed. Rebuilding FFmpeg with a
modified dav1d or zlib can use the complete sources included here.

## Provenance limits

The upstream workflow uses `ubuntu-latest` and repository toolchain packages;
it does not pin a complete distribution/toolchain snapshot. DLL strings identify
GCC 13 (win32 threading model) and zlib 1.3.1, but the exact distribution patch
level is not established. These are matching upstream sources and build
instructions, not a claim that a rebuild will produce byte-identical binaries.

ADB 37.0.1 and its two Windows API DLLs come from Google's platform-tools ZIP
whose hash is pinned by scrcpy. The original complete `NOTICE.txt` is included
as `ADB-37.0.1-NOTICE.txt`. ADB source is available from
[AOSP](https://android.googlesource.com/platform/packages/modules/adb/), and the
Windows USB API source is in
[platform/development](https://android.googlesource.com/platform/development/+/refs/heads/main/host/windows/usb/).
The precise AOSP source revision used for Google's binary is not identified by
scrcpy's dependency script. These permissively licensed components are retained
with their supplied notices; this document does not assert an exact AOSP commit.

SDL 3.4.18 source is available at its
[pinned upstream tag](https://github.com/libsdl-org/SDL/tree/release-3.4.18).
Its download URL and expected checksum remain in the upstream build scripts.

No dependency executable, build script, test, or phone session was executed to
prepare these notices. Identification used upstream scripts, binary metadata,
embedded strings and archive contents.
