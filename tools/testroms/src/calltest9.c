/* ExecutionTracker test, ARM9: nested calls, an ARM -> Thumb call, recursion.
 *   RESULT9(1..4) addresses of level1, level2, thumb_level3, wait_here
 *   RESULT9(5)    the test writes 1 to let wait_here return
 *   RESULT9(6)    address of leaf (called 10 times), RESULT9(7) address of fact (called 6 times)
 *   RESULT9(8)    fact(5) = 120
 * While wait_here spins the call stack is main > level1 > level2 > thumb_level3 > wait_here. */
#include "../nds.h"

__attribute__((noinline)) static void wait_here(void)
{
    while (RESULT9(5) != 1) {}
}

__attribute__((noinline, target("thumb"))) static void thumb_level3(void)
{
    wait_here();
    __asm__ volatile("" ::: "memory");
}

__attribute__((noinline)) static void level2(void) { thumb_level3(); __asm__ volatile("" ::: "memory"); }
__attribute__((noinline)) static void level1(void) { level2(); __asm__ volatile("" ::: "memory"); }

__attribute__((noinline)) static int leaf(int x) { return x + 1; }

__attribute__((noinline, optnone)) static int fact(int n) { return n <= 1 ? 1 : n * fact(n - 1); }

int main(void)
{
    RESULT9(1) = (u32)level1;
    RESULT9(2) = (u32)level2;
    RESULT9(3) = (u32)thumb_level3;
    RESULT9(4) = (u32)wait_here;
    RESULT9(6) = (u32)leaf;
    RESULT9(7) = (u32)fact;
    level1();
    volatile int sum = 0;
    for (int i = 0; i < 10; i++)
        sum = leaf(sum);
    RESULT9(8) = fact(5);
    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
