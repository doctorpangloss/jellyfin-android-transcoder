#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FFMPEG_SRC="${FFMPEG_SRC:-$ROOT/../forks-ffmpeg-android}"
NDK_ROOT="${ANDROID_NDK_ROOT:-${ANDROID_NDK_HOME:-/home/administrator/android-ndk/android-ndk-r27d}}"
API="${ANDROID_API:-28}"
OUT_DIR="${OUT_DIR:-$ROOT/android-transcoder/app/src/main/jniLibs}"
JOBS="${JOBS:-$(nproc)}"
ABIS="${ABIS:-arm64-v8a armeabi-v7a x86 x86_64}"

if [[ ! -x "$FFMPEG_SRC/configure" ]]; then
  cat >&2 <<MSG
Missing FFmpeg source at $FFMPEG_SRC.
Set FFMPEG_SRC to a checkout of doctorpangloss/forks-ffmpeg-android on branch mediacodec-surface-hwframes.
MSG
  exit 1
fi

TOOLCHAIN="$NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64"
if [[ ! -d "$TOOLCHAIN" ]]; then
  echo "Missing Android NDK LLVM toolchain at $TOOLCHAIN" >&2
  exit 1
fi

mkdir -p "$OUT_DIR" "$ROOT/.work"

build_one() {
  local abi="$1"
  local ff_arch="$2"
  local cpu="$3"
  local cc_prefix="$4"
  local extra_cflags="$5"
  local extra_configure="${6:-}"
  local build_dir="$ROOT/.work/ffmpeg-$abi"
  local install_dir="$ROOT/.work/install-$abi"

  rm -rf "$build_dir" "$install_dir" "$OUT_DIR/$abi"
  mkdir -p "$build_dir" "$install_dir" "$OUT_DIR/$abi"

  pushd "$build_dir" >/dev/null
  PATH="$TOOLCHAIN/bin:$PATH" "$FFMPEG_SRC/configure" \
    --prefix="$install_dir" \
    --target-os=android \
    --arch="$ff_arch" \
    --cpu="$cpu" \
    --cc="$cc_prefix${API}-clang" \
    --cxx="$cc_prefix${API}-clang++" \
    --ar=llvm-ar \
    --ranlib=llvm-ranlib \
    --strip=llvm-strip \
    --sysroot="$TOOLCHAIN/sysroot" \
    --enable-cross-compile \
    --enable-static \
    --disable-shared \
    --disable-symver \
    --disable-doc \
    --disable-debug \
    --disable-avdevice \
    --disable-everything \
    --enable-ffmpeg \
    --enable-network \
    --enable-protocol='file,pipe,http,tcp' \
    --enable-demuxer='matroska,mov,mpegts' \
    --enable-muxer='hls,mpegts,null,mp4,matroska' \
    --enable-parser='hevc,h264' \
    --enable-bsf='hevc_mp4toannexb,h264_mp4toannexb,extract_extradata,h264_metadata,hevc_metadata' \
    --enable-decoder='hevc,hevc_mediacodec,h264,h264_mediacodec' \
    --enable-encoder='hevc_mediacodec,h264_mediacodec' \
    --enable-filter='null,format,scale' \
    --enable-mediacodec \
    --enable-jni \
    --enable-pic \
    --extra-cflags="$extra_cflags" \
    --extra-ldflags='-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -landroid -lmediandk -llog -lEGL -lGLESv2' \
    $extra_configure

  mkdir -p fftools/resources
  PATH="$TOOLCHAIN/bin:$PATH" make -j"$JOBS" ffmpeg
  PATH="$TOOLCHAIN/bin:$PATH" llvm-strip ffmpeg
  install -m 0755 ffmpeg "$OUT_DIR/$abi/libffmpeg.so"
  popd >/dev/null
}

for abi in $ABIS; do
  case "$abi" in
    arm64-v8a) build_one arm64-v8a aarch64 armv8-a aarch64-linux-android "" ;;
    armeabi-v7a) build_one armeabi-v7a arm armv7-a armv7a-linux-androideabi "-march=armv7-a -mfpu=neon -mfloat-abi=softfp" ;;
    x86) build_one x86 x86 i686 i686-linux-android "-march=i686 -mtune=atom -mssse3 -mfpmath=sse" "--disable-asm" ;;
    x86_64) build_one x86_64 x86_64 x86-64 x86_64-linux-android "-msse4.2" ;;
    *) echo "Unknown ABI: $abi" >&2; exit 1 ;;
  esac
done

find "$OUT_DIR" -maxdepth 2 -type f -name libffmpeg.so -print -exec file {} \;
