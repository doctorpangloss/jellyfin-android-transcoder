#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FFMPEG_SRC="${FFMPEG_SRC:-$ROOT/../forks-ffmpeg-android}"
NDK_ROOT="${ANDROID_NDK_ROOT:-${ANDROID_NDK_HOME:-/home/administrator/android-ndk/android-ndk-r27d}}"
API="${ANDROID_API:-28}"
OUT_DIR="${OUT_DIR:-$ROOT/android-transcoder/app/src/main/jniLibs}"
JOBS="${JOBS:-$(nproc)}"
ABIS="${ABIS:-arm64-v8a armeabi-v7a x86 x86_64}"
MBEDTLS_VERSION="${MBEDTLS_VERSION:-3.6.6}"
MBEDTLS_SRC="$ROOT/.work/mbedtls-$MBEDTLS_VERSION"

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

ensure_mbedtls_source() {
  if [[ -d "$MBEDTLS_SRC" ]]; then
    return
  fi

  git clone --depth 1 --branch "v$MBEDTLS_VERSION" --recurse-submodules --shallow-submodules \
    https://github.com/Mbed-TLS/mbedtls.git "$MBEDTLS_SRC"
}

build_mbedtls() {
  local abi="$1"
  local android_abi="$2"
  local install_dir="$ROOT/.work/mbedtls-install-$abi"
  local build_dir="$ROOT/.work/mbedtls-build-$abi"

  ensure_mbedtls_source
  rm -rf "$build_dir" "$install_dir"
  mkdir -p "$build_dir" "$install_dir"

  cmake -S "$MBEDTLS_SRC" -B "$build_dir" \
    -DCMAKE_TOOLCHAIN_FILE="$NDK_ROOT/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$android_abi" \
    -DANDROID_PLATFORM="android-$API" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$install_dir" \
    -DENABLE_PROGRAMS=OFF \
    -DENABLE_TESTING=OFF \
    -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
    -DUSE_STATIC_MBEDTLS_LIBRARY=ON
  cmake --build "$build_dir" --target install --parallel "$JOBS"
}

build_one() {
  local abi="$1"
  local ff_arch="$2"
  local cpu="$3"
  local cc_prefix="$4"
  local extra_cflags="$5"
  local android_abi="$6"
  local extra_configure="${7:-}"
  local build_dir="$ROOT/.work/ffmpeg-$abi"
  local install_dir="$ROOT/.work/install-$abi"
  local mbedtls_install="$ROOT/.work/mbedtls-install-$abi"

  rm -rf "$build_dir" "$install_dir" "$OUT_DIR/$abi"
  mkdir -p "$build_dir" "$install_dir" "$OUT_DIR/$abi"
  if [[ ! -f "$mbedtls_install/lib/libmbedtls.a" ]]; then
    build_mbedtls "$abi" "$android_abi"
  fi

  pushd "$build_dir" >/dev/null
  export PKG_CONFIG_LIBDIR="$mbedtls_install/lib/pkgconfig"
  export PKG_CONFIG_PATH="$mbedtls_install/lib/pkgconfig"
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
    --enable-version3 \
    --enable-ffmpeg \
    --enable-network \
    --enable-mbedtls \
    --enable-protocol='file,pipe,http,https,tcp,tls' \
    --enable-demuxer='matroska,mov,mpegts' \
    --enable-muxer='hls,segment,mpegts,null,mp4,matroska' \
    --enable-parser='hevc,h264,aac,ac3,mlp' \
    --enable-bsf='hevc_mp4toannexb,h264_mp4toannexb,extract_extradata,h264_metadata,hevc_metadata' \
    --enable-decoder='hevc,hevc_mediacodec,h264,h264_mediacodec,aac,ac3,eac3,truehd,flac,mp3' \
    --enable-encoder='hevc_mediacodec,h264_mediacodec,aac' \
    --enable-filter='null,format,scale,aresample,aformat,volume' \
    --enable-swresample \
    --enable-mediacodec \
    --enable-jni \
    --enable-pic \
    --extra-cflags="$extra_cflags -I$mbedtls_install/include" \
    --extra-ldflags="-L$mbedtls_install/lib -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -landroid -lmediandk -llog -lEGL -lGLESv2" \
    --extra-libs='-lmbedtls -lmbedx509 -lmbedcrypto' \
    $extra_configure

  mkdir -p fftools/resources
  PATH="$TOOLCHAIN/bin:$PATH" make -j"$JOBS" ffmpeg
  PATH="$TOOLCHAIN/bin:$PATH" llvm-strip ffmpeg
  install -m 0755 ffmpeg "$OUT_DIR/$abi/libffmpeg.so"
  popd >/dev/null
}

for abi in $ABIS; do
  case "$abi" in
    arm64-v8a) build_one arm64-v8a aarch64 armv8-a aarch64-linux-android "" arm64-v8a ;;
    armeabi-v7a) build_one armeabi-v7a arm armv7-a armv7a-linux-androideabi "-march=armv7-a -mfpu=neon -mfloat-abi=softfp" armeabi-v7a ;;
    x86) build_one x86 x86 i686 i686-linux-android "-march=i686 -mtune=atom -mssse3 -mfpmath=sse" x86 "--disable-asm" ;;
    x86_64) build_one x86_64 x86_64 x86-64 x86_64-linux-android "-msse4.2" x86_64 ;;
    *) echo "Unknown ABI: $abi" >&2; exit 1 ;;
  esac
done

find "$OUT_DIR" -maxdepth 2 -type f -name libffmpeg.so -print -exec file {} \;
