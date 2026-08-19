#define MINIMP3_IMPLEMENTATION
#define MINIMP3_NO_SIMD
#define MINIMP3_ONLY_MP3
#include "minimp3.h"
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: decode in.mp3 out.pcm\n"); return 2; }

    FILE *f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", argv[1]); return 1; }
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char *buf = (unsigned char *)malloc(sz ? sz : 1);
    if (sz) fread(buf, 1, sz, f);
    fclose(f);

    mp3dec_t dec;
    mp3dec_init(&dec);
    mp3dec_frame_info_t info;
    short pcm[MINIMP3_MAX_SAMPLES_PER_FRAME];

    FILE *out = fopen(argv[2], "wb");
    if (!out) { fprintf(stderr, "cannot open %s\n", argv[2]); return 1; }

    long pos = 0;
    while (pos < sz)
    {
        int samples = mp3dec_decode_frame(&dec, buf + pos, (int)(sz - pos), pcm, &info);
        if (info.frame_bytes == 0) break;
        if (samples > 0)
            fwrite(pcm, sizeof(short), (size_t)(samples * info.channels), out);
        pos += info.frame_bytes;
    }
    fclose(out);
    free(buf);
    return 0;
}
