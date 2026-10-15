#pragma once
#include <stdint.h>
#include <stddef.h>
// All functions on a bridge are serialized on its creator thread.
extern "C" {
__declspec(dllexport) void* db_graphics_create(const char* config_utf8, void* hwnd);
__declspec(dllexport) int db_graphics_reconfigure(void* session, const char* config_utf8);
__declspec(dllexport) int db_graphics_frame(void* session, const uint8_t* bgra, size_t capacity,
 uint32_t width, uint32_t height, const char* settings_utf8, uint8_t* output, size_t output_capacity);
__declspec(dllexport) size_t db_graphics_status(void* session, char* destination, size_t capacity);
__declspec(dllexport) const char* db_graphics_error();
__declspec(dllexport) int db_graphics_destroy(void* session);
}
