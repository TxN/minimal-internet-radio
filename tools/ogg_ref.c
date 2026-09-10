// Reference Ogg Vorbis decoder harness for validating the C# port.
//
// It uses the stb_vorbis PUSH api (the same code path the C# port implements) and
// writes interleaved 16-bit little-endian PCM, so its output can be compared
// byte-for-byte against:
//     tests\InternetRadio.Tests -- decode in.ogg out.pcm
//
// The float->short conversion is copied verbatim from stb_vorbis
// (convert_channels_short_interleaved + FAST_SCALED_FLOAT_TO_INT): that is the
// convention the port has to reproduce.
//
// NOTE: stb's two conversion paths are NOT bit-identical. The default fast path
// (union trick) rounds to nearest, the plain path truncates toward zero; on a real
// file they differ by exactly 1 LSB on ~48% of samples (measured on crow_cry.ogg).
// The golden reference therefore has to be built WITH /DSTB_VORBIS_NO_FAST_SCALED_FLOAT,
// and the C# port must truncate: (int)(x * 32768f) plus the same clamp.
//
// Build (MSVC), from the repository root:
//   cl /nologo /O2 /W3 /DSTB_VORBIS_NO_FAST_SCALED_FLOAT ^
//      /I C:\TestWorkspace\reference /Fo:C:\TestWorkspace\build\ /Fe:C:\TestWorkspace\build\ogg_ref.exe tools\ogg_ref.c
//   STB_VORBIS_NO_STDIO is defined in this file.
//
// Usage:
//   ogg_ref in.ogg out.pcm [chunkBytes]
//   chunkBytes (default 4096) is the push window, so the streaming path is exercised
//   exactly like the library feeds the decoder.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define STB_VORBIS_NO_STDIO
#include "stb_vorbis.c"

static void write_interleaved_short(FILE *out, int channels, float **output, int samples)
{
    int i, j;
    for (j = 0; j < samples; ++j)
    {
        for (i = 0; i < channels; ++i)
        {
            FASTDEF(temp);
            float f = output[i][j];
            int v = FAST_SCALED_FLOAT_TO_INT(temp, f, 15);
            if ((unsigned int)(v + 32768) > 65535)
                v = v < 0 ? -32768 : 32767;
            short s = (short)v;
            if (fwrite(&s, sizeof(short), 1, out) != 1)
            {
                fprintf(stderr, "write error\n");
                exit(1);
            }
        }
    }
}

int main(int argc, char **argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: ogg_ref in.ogg out.pcm [chunkBytes]\n");
        return 2;
    }

    int chunk = (argc > 3) ? atoi(argv[3]) : 4096;
    if (chunk < 64) chunk = 64;

    FILE *f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", argv[1]); return 1; }
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char *buf = (unsigned char *)malloc(sz > 0 ? (size_t)sz : 1);
    if (sz > 0 && fread(buf, 1, (size_t)sz, f) != (size_t)sz)
    {
        fprintf(stderr, "read error\n");
        return 1;
    }
    fclose(f);

    FILE *out = fopen(argv[2], "wb");
    if (!out) { fprintf(stderr, "cannot open %s\n", argv[2]); return 1; }

    int error = 0;
    int used = 0;
    stb_vorbis *v = stb_vorbis_open_pushdata(buf, (int)sz, &used, &error, NULL);
    if (v == NULL)
    {
        fprintf(stderr, "open_pushdata failed: error=%d (VORBIS_need_more_data=%d)\n",
                error, VORBIS_need_more_data);
        fclose(out);
        free(buf);
        return 1;
    }

    stb_vorbis_info info = stb_vorbis_get_info(v);
    long long total = 0;

    /* Sliding push window: [start, end) inside buf. When the decoder asks for more
       data it gets the same window plus another chunk, which is the documented
       push-api contract. */
    long start = used;
    long end = start + chunk;
    if (end > sz) end = sz;

    for (;;)
    {
        int channels = 0, samples = 0;
        float **output = NULL;

        if (start >= sz && end <= start)
            break;

        used = stb_vorbis_decode_frame_pushdata(v, buf + start, (int)(end - start),
                                                &channels, &output, &samples);

        if (used == 0 && samples == 0)
        {
            if (end >= sz)
                break;                    /* no more input can be supplied */
            end += chunk;                 /* same data plus more */
            if (end > sz) end = sz;
            continue;
        }

        start += used;
        if (samples > 0)
        {
            write_interleaved_short(out, info.channels, output, samples);
            total += (long long)samples * info.channels;
        }

        /* Keep at least `chunk` bytes available for the next call. */
        if (end < sz)
        {
            end = start + chunk;
            if (end > sz) end = sz;
        }
    }

    stb_vorbis_close(v);
    fclose(out);
    free(buf);

    printf("ogg_ref: %d ch, %d Hz, %lld interleaved samples, %d bytes consumed\n",
           info.channels, info.sample_rate, total, (int)start);
    return 0;
}
