/* Functions the compiler may emit calls to. */
#include "nds.h"
typedef unsigned long size_t;
void *memcpy(void *d, const void *s, size_t n) { u8 *dd = d; const u8 *ss = s; while (n--) *dd++ = *ss++; return d; }
void *memset(void *d, int v, size_t n) { u8 *dd = d; while (n--) *dd++ = (u8)v; return d; }
void *memmove(void *d, const void *s, size_t n) { u8 *dd = d; const u8 *ss = s; if (dd < ss) while (n--) *dd++ = *ss++; else { dd += n; ss += n; while (n--) *--dd = *--ss; } return d; }
void __aeabi_memcpy(void *d, const void *s, size_t n) { memcpy(d, s, n); }
void __aeabi_memcpy4(void *d, const void *s, size_t n) { memcpy(d, s, n); }
void __aeabi_memset(void *d, size_t n, int v) { memset(d, v, n); }
void __aeabi_memclr(void *d, size_t n) { memset(d, 0, n); }
void __aeabi_memclr4(void *d, size_t n) { memset(d, 0, n); }
