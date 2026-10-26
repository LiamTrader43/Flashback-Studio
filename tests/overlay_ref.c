/* Flashback Studio's input display, as the 3DS app draws it, for the converter
   test to compare the converter's with: run_tests.py copies main.c's drawing
   code (from "// The input display:" to the end of drawInputs), unchanged, into
   build/overlay_extracted.c, which this includes.

   usage: overlay_ref <inputs> <out>
   Reads 16-byte inputs records (as in a clip), and for each one draws the
   display over the background below and writes the top screen: 400 x 240
   RGB565, top row first. */
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "flb.h"

typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;
#define TOP_W 400
#define TOP_H 240
enum { KEY_A = 1, KEY_B = 2, KEY_SELECT = 4, KEY_START = 8, KEY_DRIGHT = 16, KEY_DLEFT = 32, KEY_DUP = 64,
       KEY_DDOWN = 128, KEY_R = 256, KEY_L = 512, KEY_X = 1024, KEY_Y = 2048, KEY_TOUCH = 1 << 20 };

#include "overlay_extracted.c"

/* (converter_test.cs draws on the same) */
static u16 background(u32 x, u32 y)
{
    return (u16)((x * 31 / 399) << 11 | (y * 63 / 239) << 5 | ((x ^ y) & 31));
}

int main(int argc, char **argv)
{
    (void)padSaveUnder;
    (void)padRestoreUnder;
    if (argc != 3)
        return 2;
    FILE *in = fopen(argv[1], "rb"), *out = fopen(argv[2], "wb");
    if (in == NULL || out == NULL)
        return 1;
    static u16 fb[TOP_W * TOP_H], screen[TOP_W * TOP_H];
    uint8_t r[16];
    while (fread(r, 1, sizeof(r), in) == sizeof(r))
    {
        FlbInput input;
        input.buttons = r[0] | r[1] << 8 | r[2] << 16 | (u32)r[3] << 24;
        input.circleX = (int16_t)(r[4] | r[5] << 8);
        input.circleY = (int16_t)(r[6] | r[7] << 8);
        input.touchX = (uint16_t)(r[8] | r[9] << 8);
        input.touchY = (uint16_t)(r[10] | r[11] << 8);
        input.cstickX = (int16_t)(r[12] | r[13] << 8);
        input.cstickY = (int16_t)(r[14] | r[15] << 8);
        /* (the top screen's framebuffer is rotated: x columns of 240, bottom row first) */
        for (u32 x = 0; x < TOP_W; x++)
            for (u32 y = 0; y < TOP_H; y++)
                fb[x * TOP_H + (TOP_H - 1 - y)] = background(x, y);
        drawInputs(fb, &input);
        for (u32 x = 0; x < TOP_W; x++)
            for (u32 y = 0; y < TOP_H; y++)
                screen[y * TOP_W + x] = fb[x * TOP_H + (TOP_H - 1 - y)];
        fwrite(screen, 2, TOP_W * TOP_H, out);
    }
    fclose(in);
    return fclose(out) != 0;
}
