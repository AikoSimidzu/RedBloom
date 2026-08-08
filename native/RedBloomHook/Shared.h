// Layout of the shared memory block the hook writes and RedBloom reads.
//
// The hook lives inside Wallpaper Engine, so it is the side that creates the mapping: a
// medium-integrity process cannot open an object made by an elevated one, but an elevated
// RedBloom can always open this. Getting that the wrong way round would mean RedBloom only
// worked when it was not elevated.
#pragma once

#include <cstdint>

#define REDBLOOM_MAP_NAME L"Local\\RedBloomWallpaperFrame"
#define REDBLOOM_MAGIC 0x50574252u  // "RBWP" little-endian

// 1440x900 BGRA is 5 MB, and the picture is blurred behind a terminal anyway. Two of these
// rotate so the reader is never parsing the buffer the hook is filling.
#define REDBLOOM_MAX_WIDTH 1920
#define REDBLOOM_MAX_HEIGHT 1200
#define REDBLOOM_BUFFER_BYTES (REDBLOOM_MAX_WIDTH * REDBLOOM_MAX_HEIGHT * 4)
#define REDBLOOM_BUFFER_COUNT 2

struct RedBloomFrameHeader
{
    uint32_t Magic;

    // Written by the hook.
    uint32_t Width;
    uint32_t Height;
    uint32_t Stride;

    // 0 = BGRA, 1 = RGBA. The swap chain picks its own order and we convert on the far side
    // rather than paying for a per-pixel swizzle inside someone else's render loop.
    uint32_t Channels;

    uint32_t Latest;       // index of the buffer holding the newest complete frame
    uint64_t FrameIndex;   // increments per published frame, so the reader can skip repeats

    // Written by RedBloom: how often the hook is allowed to copy, and a heartbeat. The hook
    // stops copying when nobody has asked for a frame recently, so an abandoned injection
    // costs Wallpaper Engine nothing.
    uint32_t IntervalMs;
    uint64_t ReaderTickMs;

    uint32_t Reserved[8];
};

#define REDBLOOM_HEADER_BYTES 4096
#define REDBLOOM_TOTAL_BYTES (REDBLOOM_HEADER_BYTES + REDBLOOM_BUFFER_BYTES * REDBLOOM_BUFFER_COUNT)
