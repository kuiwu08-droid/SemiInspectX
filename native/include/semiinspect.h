#pragma once
#include <stdint.h>
#ifdef _WIN32
#ifdef SI_BUILD
#define SI_API __declspec(dllexport)
#else
#define SI_API __declspec(dllimport)
#endif
#else
#define SI_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* ABI v1: default 8-byte packing, cdecl. No STL types cross this boundary. */
typedef struct { int32_t x, y, width, height; } si_rect;
typedef struct {
    si_rect marker, inspection, measurement;
    int32_t max_shift, threshold, min_area;
    double min_alignment, min_width, max_width, microns_per_pixel;
} si_recipe;
typedef struct { int32_t x, y, width, height, area; } si_defect;
typedef struct {
    int32_t shift_x, shift_y, defect_count, passed;
    double alignment, width_pixels, width_microns, milliseconds;
} si_summary;
typedef void* si_engine;
typedef void* si_result;
/* 0=OK, 1=invalid input, 2=alignment failure, 3=internal failure. */
SI_API int32_t si_version(void);
SI_API int32_t si_create(const uint8_t* reference, int32_t width, int32_t height,
                         int32_t stride, const si_recipe* recipe, si_engine* engine);
SI_API void si_destroy(si_engine engine);
/* Borrowed input buffer; synchronous call. Returned result must be released. */
SI_API int32_t si_inspect(si_engine engine, const uint8_t* pixels, int32_t width,
                          int32_t height, int32_t stride, si_result* result);
SI_API int32_t si_get_summary(si_result result, si_summary* summary);
SI_API int32_t si_get_defect(si_result result, int32_t index, si_defect* defect);
SI_API void si_release_result(si_result result);
#ifdef __cplusplus
}
#endif
