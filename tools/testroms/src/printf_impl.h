/* A small printf for the printf hook test ROMs (printf9.c, printf7.c). */
#ifndef PRINTF_IMPL_H
#define PRINTF_IMPL_H

typedef __builtin_va_list va_list;
#define va_start(v, l) __builtin_va_start(v, l)
#define va_end(v) __builtin_va_end(v)
#define va_arg(v, t) __builtin_va_arg(v, t)

/* printf7.c builds the functions as Thumb code. */
#ifndef PRINTF_ATTR
#define PRINTF_ATTR
#endif

/* 64-bit divide by a small base, without a runtime helper. */
PRINTF_ATTR static unsigned divmod64(unsigned long long *v, unsigned base)
{
    /* On 32-bit halves: Thumb 64-bit shifts would need runtime helpers too. */
    unsigned hi = (unsigned)(*v >> 32), lo = (unsigned)*v, qh = 0, ql = 0, r = 0;
    for (int bit = 31; bit >= 0; bit--) {
        r = r << 1 | (hi >> bit & 1);
        if (r >= base) { r -= base; qh |= 1u << bit; }
    }
    for (int bit = 31; bit >= 0; bit--) {
        r = r << 1 | (lo >> bit & 1);
        if (r >= base) { r -= base; ql |= 1u << bit; }
    }
    *v = (unsigned long long)qh << 32 | ql;
    return r;
}

PRINTF_ATTR __attribute__((noinline)) int fmt_vsnprintf(char *out, int size, const char *fmt, va_list ap)
{
    int n = 0;
#define PUT(ch) do { if (n + 1 < size) out[n] = (ch); n++; } while (0)
    for (; *fmt; fmt++) {
        if (*fmt != '%') { PUT(*fmt); continue; }
        fmt++;
        int left = 0, plus = 0, zero = 0, width = 0, ll = 0;
        for (;; fmt++) {
            if (*fmt == '-') left = 1;
            else if (*fmt == '+') plus = 1;
            else if (*fmt == '0') zero = 1;
            else break;
        }
        while (*fmt >= '1' && *fmt <= '9') width = width * 10 + *fmt++ - '0';
        while (*fmt == 'l') { ll++; fmt++; }
        char tmp[24];
        const char *s = tmp;
        int len = 0, neg = 0;
        unsigned long long v = 0;
        int base = 10, upper = 0;
        char c = *fmt;
        if (c == 'd' || c == 'i') {
            long long x = ll >= 2 ? va_arg(ap, long long) : va_arg(ap, int);
            neg = x < 0;
            v = neg ? -x : x;
        } else if (c == 'u' || c == 'x' || c == 'X') {
            v = ll >= 2 ? va_arg(ap, unsigned long long) : va_arg(ap, unsigned);
            base = c == 'u' ? 10 : 16;
            upper = c == 'X';
        } else if (c == 's') {
            s = va_arg(ap, const char *);
            while (s[len]) len++;
        } else if (c == 'c') {
            tmp[0] = (char)va_arg(ap, int);
            len = 1;
        } else {
            PUT(c);
            continue;
        }
        if (c != 's' && c != 'c') {
            char rev[24];
            int r = 0;
            do { unsigned d = divmod64(&v, base); rev[r++] = (char)(d < 10 ? '0' + d : (upper ? 'A' : 'a') + d - 10); } while (v);
            if (neg) tmp[len++] = '-';
            else if (plus) tmp[len++] = '+';
            while (r) tmp[len++] = rev[--r];
        }
        int pad = width - len;
        if (!left) while (pad-- > 0) PUT(zero ? '0' : ' ');
        if (zero && !left && (neg || plus)) { /* sign before zeros: rare, keep simple */ }
        for (int i = 0; i < len; i++) PUT(s[i]);
        if (left) while (pad-- > 0) PUT(' ');
    }
    if (size > 0) out[n < size ? n : size - 1] = 0;
    return n;
#undef PUT
}

PRINTF_ATTR __attribute__((noinline)) int game_sprintf(char *out, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int n = fmt_vsnprintf(out, 0x7FFFFFFF, fmt, ap);
    va_end(ap);
    return n;
}

PRINTF_ATTR __attribute__((noinline)) int game_printf(const char *fmt, ...)
{
    static char line[128];
    va_list ap;
    va_start(ap, fmt);
    int n = fmt_vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);
    return n; /* a retail build sends this nowhere */
}

#endif
