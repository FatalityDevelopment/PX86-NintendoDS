using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Cpu.Arm;
using Px86.NintendoDS.Common.Cpu.Thumb;
using Px86.NintendoDS.EmulatorCore.Hardware;

namespace Px86.NintendoDS.EmulatorCore.Jit
{
    /// <summary>A compiled block. Returns the new CPU time, like <c>NdsEmulator.RunBlock</c>.</summary>
    public delegate long JitBlockFunc(ArmCpu cpu, CachedBlock block, Scheduler scheduler, DmaController dma, long time, long target);

    /// <summary>
    /// Turns a <see cref="CachedBlock"/> into IL (a <see cref="DynamicMethod"/>),
    /// which .NET compiles to native code.
    ///
    /// The compiled block keeps the interpreter's results exactly: the same
    /// cycles per instruction, and it stops after the same instruction the
    /// block-cache loop would (slice end, jump, halt, pending IRQ, overwritten
    /// code, ARM/Thumb switch).
    ///
    ///   Inlined     ARM data processing (immediate / immediate shift, Rd ≠ PC),
    ///               B/BL; Thumb formats 1–5 (except MUL and PC writes), 12, 13,
    ///               conditional and unconditional B. Only the time limit can
    ///               stop the block after these: they touch nothing but
    ///               registers and flags.
    ///               Single loads and stores (ARM LDR/STR{B,H,SB,SH} without PC
    ///               loads or PC writeback; Thumb formats 6–11). Devices react to
    ///               these, so they get the exit checks below, minus the PC one.
    ///   Everything  A direct call to the interpreter handler, wrapped in
    ///   else        <see cref="ArmCpu.JitBegin"/> / <see cref="ArmCpu.JitEnd"/>,
    ///               followed by the full set of exit checks.
    ///
    /// PC, R15 and the cycle counter are only brought up to date when the
    /// block leaves (<see cref="ArmCpu.JitRetire"/>) or before a handler call.
    /// </summary>
    public static class JitCompiler
    {
        private static readonly Type[] Parameters =
            { typeof(ArmCpu), typeof(CachedBlock), typeof(Scheduler), typeof(DmaController), typeof(long), typeof(long) };

        private const int ArgCpu = 0, ArgBlock = 1, ArgScheduler = 2, ArgDma = 3, ArgTime = 4, ArgTarget = 5;

        private static readonly FieldInfo FieldR = typeof(ArmCpu).GetField(nameof(ArmCpu.R))!;
        private static readonly FieldInfo FieldValid = typeof(CachedBlock).GetField(nameof(CachedBlock.Valid))!;
        private static readonly MethodInfo Condition = typeof(ArmCpu).GetMethod(nameof(ArmCpu.ConditionPassed))!;
        private static readonly MethodInfo GetFlagC = typeof(ArmCpu).GetProperty(nameof(ArmCpu.FlagC))!.GetMethod!;
        private static readonly MethodInfo GetPc = typeof(ArmCpu).GetProperty(nameof(ArmCpu.Pc))!.GetMethod!;
        private static readonly MethodInfo GetHalted = typeof(ArmCpu).GetProperty(nameof(ArmCpu.Halted))!.GetMethod!;
        private static readonly MethodInfo GetIrqPending = typeof(ArmCpu).GetProperty(nameof(ArmCpu.IrqPending))!.GetMethod!;
        private static readonly MethodInfo GetIsThumb = typeof(ArmCpu).GetProperty(nameof(ArmCpu.IsThumb))!.GetMethod!;
        private static readonly MethodInfo Begin = typeof(ArmCpu).GetMethod(nameof(ArmCpu.JitBegin))!;
        private static readonly MethodInfo End = typeof(ArmCpu).GetMethod(nameof(ArmCpu.JitEnd))!;
        private static readonly MethodInfo Retire = typeof(ArmCpu).GetMethod(nameof(ArmCpu.JitRetire))!;
        private static readonly MethodInfo AddCycles = typeof(ArmCpu).GetMethod(nameof(ArmCpu.JitAddCycles))!;
        private static readonly MethodInfo Unconditional = typeof(ArmInstructionTable).GetMethod(nameof(ArmInstructionTable.ExecuteUnconditional))!;
        private static readonly MethodInfo SetTime = typeof(Scheduler).GetProperty(nameof(Scheduler.CurrentTime))!.SetMethod!;
        private static readonly MethodInfo GetDmaCycles = typeof(DmaController).GetProperty(nameof(DmaController.PendingCycles))!.GetMethod!;
        private static readonly MethodInfo SetDmaCycles = typeof(DmaController).GetProperty(nameof(DmaController.PendingCycles))!.SetMethod!;
        private static readonly MethodInfo ShiftImmediateCarry = typeof(Alu).GetMethod(nameof(Alu.ShiftImmediate))!;
        private static readonly FieldInfo ArmHandlers = typeof(CachedBlock).GetField(nameof(CachedBlock.ArmHandlers))!;
        private static readonly FieldInfo ThumbHandlers = typeof(CachedBlock).GetField(nameof(CachedBlock.ThumbHandlers))!;

        private static MethodInfo Op(string name) => typeof(JitOps).GetMethod(name)!;

        private static readonly MethodInfo[] DataOps =
            { Op("And"), Op("Eor"), Op("Sub"), Op("Rsb"), Op("Add"), Op("Adc"), Op("Sbc"), Op("Rsc"),
              null!, null!, null!, null!, Op("Orr"), Op("Mov"), Op("Bic"), Op("Mvn") };

        private static readonly MethodInfo[] DataOpsS =
            { Op("Ands"), Op("Eors"), Op("Subs"), Op("Rsbs"), Op("Adds"), Op("Adcs"), Op("Sbcs"), Op("Rscs"),
              Op("Tst"), Op("Teq"), Op("Cmp"), Op("Cmn"), Op("Orrs"), Op("Movs"), Op("Bics"), Op("Mvns") };

        private static readonly MethodInfo[] ThumbAluOps =
            { Op("TAnd"), Op("TEor"), null!, null!, null!, Op("TAdc"), Op("TSbc"), null!,
              Op("TTst"), Op("TNeg"), Op("Cmp"), Op("Cmn"), Op("TOrr"), null!, Op("TBic"), Op("TMvn") };

        /// <summary>Compile <paramref name="block"/>; <paramref name="clock"/> is 1 (ARM9) or 2 (ARM7) scheduler cycles per CPU cycle.</summary>
        public static JitBlockFunc Compile(CachedBlock block, int clock)
        {
            var method = new DynamicMethod($"jit_{block.Address:X8}{(block.Thumb ? "_t" : "")}", typeof(long), Parameters,
                typeof(JitCompiler).Module, skipVisibility: true);
            var e = new Emitter(method.GetILGenerator(), block, clock);
            e.EmitBlock();
            return method.CreateDelegate<JitBlockFunc>();
        }

        private sealed class Emitter
        {
            private readonly ILGenerator _il;
            private readonly CachedBlock _block;
            private readonly int _clock;
            private readonly LocalBuilder _inlined; // cycles of inlined instructions not yet added to TotalCycles
            private readonly LocalBuilder _carry;
            private readonly int _size;
            private readonly uint _pipeline;

            public Emitter(ILGenerator il, CachedBlock block, int clock)
            {
                _il = il;
                _block = block;
                _clock = clock;
                _inlined = il.DeclareLocal(typeof(int));
                _carry = il.DeclareLocal(typeof(bool));
                _size = block.Thumb ? 2 : 4;
                _pipeline = block.Thumb ? 4u : 8u;
            }

            public void EmitBlock()
            {
                for (int i = 0; i < _block.Count; i++)
                {
                    uint address = _block.Address + (uint)(i * _size);
                    uint op = _block.Opcodes[i];
                    bool inlined = _block.Thumb ? TryThumb(address, op) : TryArm(address, op);
                    if (!inlined)
                        EmitHandlerCall(i, address, op);
                }
                // Ran off the end of the block: carry on at the next address.
                uint next = _block.Address + (uint)(_block.Count * _size);
                EmitRetire(next, next - (uint)_size + _pipeline);
            }

            // ── Shared pieces ─────────────────────────────────────────────────

            /// <summary>time += cycles × clock; inlined += cycles.</summary>
            private void EmitCycles(int cycles)
            {
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldc_I8, (long)cycles * _clock);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Starg, ArgTime);
                _il.Emit(OpCodes.Ldloc, _inlined);
                _il.Emit(OpCodes.Ldc_I4, cycles);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Stloc, _inlined);
            }

            /// <summary>Leave with PC = <paramref name="next"/> and R15 as the last instruction saw it.</summary>
            private void EmitRetire(uint next, uint r15)
            {
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)next));
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)r15));
                _il.Emit(OpCodes.Ldloc, _inlined);
                _il.Emit(OpCodes.Call, Retire);
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ret);
            }

            /// <summary>After an inlined instruction: stop if the slice is over.</summary>
            private void EmitTimeCheck(uint address)
            {
                var go = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgTarget);
                _il.Emit(OpCodes.Blt, go);
                EmitRetire(address + (uint)_size, address + _pipeline);
                _il.MarkLabel(go);
            }

            private void EmitReg(int index, uint address)
            {
                if (index == 15)
                {
                    _il.Emit(OpCodes.Ldc_I4, unchecked((int)(address + _pipeline)));
                    return;
                }
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldfld, FieldR);
                _il.Emit(OpCodes.Ldc_I4, index);
                _il.Emit(OpCodes.Ldelem_U4);
            }

            /// <summary>Branch to <paramref name="skip"/> when the ARM condition fails (AL emits nothing).</summary>
            private void EmitCondition(uint cond, Label skip)
            {
                if (cond == 0xE)
                    return;
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, (int)cond);
                _il.Emit(OpCodes.Call, Condition);
                _il.Emit(OpCodes.Brfalse, skip);
            }

            /// <summary>A taken jump: leave the block at the target (Branch() timing: 1 + 2 cycles).</summary>
            private void EmitTakenBranch(uint address, uint target)
            {
                EmitCycles(3);
                EmitRetire(target, address + _pipeline);
            }

            // ── Handler calls ─────────────────────────────────────────────────

            private void EmitHandlerCall(int index, uint address, uint op)
            {
                uint next = address + (uint)_size;

                // Flush the inlined cycles first: the handler may look at nothing
                // but its own state, yet keeping the counter exact is free here.
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldloc, _inlined);
                _il.Emit(OpCodes.Call, AddCycles);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Stloc, _inlined);

                // Devices see the time the instruction starts at.
                _il.Emit(OpCodes.Ldarg, ArgScheduler);
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Callvirt, SetTime);

                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)address));
                _il.Emit(OpCodes.Call, Begin);

                var done = _il.DefineLabel();
                Delegate handler = _block.Thumb ? _block.ThumbHandlers![index] : _block.ArmHandlers![index];
                bool direct = handler.Method.IsStatic && handler.Target == null;
                uint cond = op >> 28;

                if (!_block.Thumb && cond == 0xF)
                {
                    EmitHandlerInvoke(Unconditional, op);
                }
                else
                {
                    if (!_block.Thumb)
                        EmitCondition(cond, done);
                    if (direct)
                        EmitHandlerInvoke((MethodInfo)handler.Method, op);
                    else
                        EmitDelegateInvoke(index, op);
                }
                _il.MarkLabel(done);

                // time += JitEnd(next) × clock
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)next));
                _il.Emit(OpCodes.Call, End);
                if (_clock != 1)
                {
                    _il.Emit(OpCodes.Ldc_I4, _clock);
                    _il.Emit(OpCodes.Mul);
                }
                _il.Emit(OpCodes.Conv_I8);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Starg, ArgTime);

                EmitDma();

                // Exit checks, as in the block-cache loop.
                var exit = _il.DefineLabel();
                var go = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgTarget);
                _il.Emit(OpCodes.Bge, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetPc);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)next));
                _il.Emit(OpCodes.Bne_Un, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetHalted);
                _il.Emit(OpCodes.Brtrue, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetIrqPending);
                _il.Emit(OpCodes.Brtrue, exit);
                _il.Emit(OpCodes.Ldarg, ArgBlock);
                _il.Emit(OpCodes.Ldfld, FieldValid);
                _il.Emit(OpCodes.Brfalse, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetIsThumb);
                _il.Emit(_block.Thumb ? OpCodes.Brtrue : OpCodes.Brfalse, go);
                _il.MarkLabel(exit);
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(go);
            }

            /// <summary>DMA started by the instruction stalls the CPU.</summary>
            private void EmitDma()
            {
                var noDma = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg, ArgDma);
                _il.Emit(OpCodes.Callvirt, GetDmaCycles);
                _il.Emit(OpCodes.Brfalse, noDma);
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgDma);
                _il.Emit(OpCodes.Callvirt, GetDmaCycles);
                if (_clock != 1)
                {
                    _il.Emit(OpCodes.Ldc_I4, _clock);
                    _il.Emit(OpCodes.Mul);
                }
                _il.Emit(OpCodes.Conv_I8);
                _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Starg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgDma);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Callvirt, SetDmaCycles);
                _il.MarkLabel(noDma);
            }

            // ── Inlined loads and stores ──────────────────────────────────────

            /// <summary>
            /// An inlined memory access. Devices may react to it (IRQs, halt,
            /// DMA, a write over this very block), so it is followed by the same
            /// exit checks as a handler call, minus the PC check: the access
            /// itself never jumps.
            /// </summary>
            private void EmitMemory(uint address, uint cond, bool load, Action emitAccess)
            {
                var skip = _il.DefineLabel();
                var done = _il.DefineLabel();
                EmitCondition(cond, skip);

                _il.Emit(OpCodes.Ldarg, ArgScheduler);
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Callvirt, SetTime);
                emitAccess();
                EmitCycles(load ? 3 : 2);
                EmitDma();

                var exit = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg, ArgTime);
                _il.Emit(OpCodes.Ldarg, ArgTarget);
                _il.Emit(OpCodes.Bge, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetHalted);
                _il.Emit(OpCodes.Brtrue, exit);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Call, GetIrqPending);
                _il.Emit(OpCodes.Brtrue, exit);
                _il.Emit(OpCodes.Ldarg, ArgBlock);
                _il.Emit(OpCodes.Ldfld, FieldValid);
                _il.Emit(OpCodes.Brtrue, done);
                _il.MarkLabel(exit);
                EmitRetire(address + (uint)_size, address + _pipeline);

                if (cond != 0xE)
                {
                    _il.MarkLabel(skip);
                    EmitCycles(1);
                    EmitTimeCheck(address);
                }
                _il.MarkLabel(done);
            }

            /// <summary>JitOps.Load(cpu, kind, rd, rn, base, offset, up, pre, writeBack).</summary>
            private void EmitLoad(int kind, int rd, int rn, uint address, Action emitOffset, bool up, bool pre, bool writeBack)
            {
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, kind);
                _il.Emit(OpCodes.Ldc_I4, rd);
                _il.Emit(OpCodes.Ldc_I4, rn);
                EmitReg(rn, address);
                emitOffset();
                EmitBool(up);
                EmitBool(pre);
                EmitBool(writeBack);
                _il.Emit(OpCodes.Call, Op("Load"));
            }

            /// <summary>JitOps.Store(cpu, kind, value, rn, base, offset, up, pre, writeBack).</summary>
            private void EmitStore(int kind, Action emitValue, int rn, uint address, Action emitOffset, bool up, bool pre, bool writeBack)
            {
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, kind);
                emitValue();
                _il.Emit(OpCodes.Ldc_I4, rn);
                EmitReg(rn, address);
                emitOffset();
                EmitBool(up);
                EmitBool(pre);
                EmitBool(writeBack);
                _il.Emit(OpCodes.Call, Op("Store"));
            }

            private void EmitBool(bool value) => _il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);

            private Action Const(uint value) => () => _il.Emit(OpCodes.Ldc_I4, unchecked((int)value));

            private Action Reg(int index, uint address) => () => EmitReg(index, address);

            /// <summary>Rm shifted by an immediate (ARM register offsets).</summary>
            private Action ShiftedReg(uint op, uint address) => () =>
            {
                int rm = (int)(op & 0xF);
                int type = (int)(op >> 5) & 3;
                int amount = (int)(op >> 7) & 0x1F;
                if (type == Alu.SHIFT_LSL && amount == 0)
                {
                    EmitReg(rm, address);
                    return;
                }
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                EmitReg(rm, address);
                _il.Emit(OpCodes.Ldc_I4, type);
                _il.Emit(OpCodes.Ldc_I4, amount);
                _il.Emit(OpCodes.Call, Op("ShiftImm"));
            };

            private void EmitHandlerInvoke(MethodInfo method, uint op)
            {
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)op));
                _il.Emit(OpCodes.Call, method);
            }

            /// <summary>Handlers that aren't plain static methods: go through the delegate stored in the block.</summary>
            private void EmitDelegateInvoke(int index, uint op)
            {
                _il.Emit(OpCodes.Ldarg, ArgBlock);
                _il.Emit(OpCodes.Ldfld, _block.Thumb ? ThumbHandlers : ArmHandlers);
                _il.Emit(OpCodes.Ldc_I4, index);
                _il.Emit(OpCodes.Ldelem_Ref);
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)op));
                var type = _block.Thumb ? typeof(ThumbHandler) : typeof(ArmHandler);
                _il.Emit(OpCodes.Callvirt, type.GetMethod("Invoke")!);
            }

            // ── ARM ───────────────────────────────────────────────────────────

            private bool TryArm(uint address, uint op)
            {
                uint cond = op >> 28;
                if (cond == 0xF)
                    return false;
                var handler = ArmInstructionTable.Handlers[((op >> 16) & 0xFF0) | ((op >> 4) & 0xF)];

                if (handler == DataProcessing.Execute)
                    return TryDataProcessing(address, op, cond);
                if (handler == Branches.BranchWithLink)
                {
                    EmitBranchWithLink(address, op, cond);
                    return true;
                }
                if (handler == SingleDataTransfer.WordByteTransfer)
                    return TryWordByteTransfer(address, op, cond);
                if (handler == SingleDataTransfer.HalfwordTransfer)
                    return TryHalfwordTransfer(address, op, cond);
                return false;
            }

            private bool TryWordByteTransfer(uint address, uint op, uint cond)
            {
                int rn = (int)(op >> 16) & 0xF;
                int rd = (int)(op >> 12) & 0xF;
                bool pre = (op & (1u << 24)) != 0;
                bool up = (op & (1u << 23)) != 0;
                bool isByte = (op & (1u << 22)) != 0;
                bool writeBack = (op & (1u << 21)) != 0 || !pre;
                bool load = (op & (1u << 20)) != 0;
                if ((load && rd == 15) || (writeBack && rn == 15))
                    return false;
                Action offset = (op & (1u << 25)) == 0 ? Const(op & 0xFFF) : ShiftedReg(op, address);
                if (load)
                    EmitMemory(address, cond, true, () => EmitLoad(isByte ? JitOps.LoadByte : JitOps.LoadWord, rd, rn, address, offset, up, pre, writeBack));
                else
                    EmitMemory(address, cond, false, () => EmitStore(isByte ? JitOps.StoreByte : JitOps.StoreWord,
                        rd == 15 ? Const(address + 12) : Reg(rd, address), rn, address, offset, up, pre, writeBack));
                return true;
            }

            private bool TryHalfwordTransfer(uint address, uint op, uint cond)
            {
                int rn = (int)(op >> 16) & 0xF;
                int rd = (int)(op >> 12) & 0xF;
                bool pre = (op & (1u << 24)) != 0;
                bool up = (op & (1u << 23)) != 0;
                bool writeBack = (op & (1u << 21)) != 0 || !pre;
                bool load = (op & (1u << 20)) != 0;
                int sh = (int)(op >> 5) & 3;
                if ((load && rd == 15) || (writeBack && rn == 15) || (!load && sh != 1))
                    return false; // PC loads, LDRD/STRD
                Action offset = (op & (1u << 22)) != 0 ? Const(((op >> 4) & 0xF0) | (op & 0xF)) : Reg((int)(op & 0xF), address);
                if (load)
                {
                    int kind = sh == 1 ? JitOps.LoadHalf : sh == 2 ? JitOps.LoadSignedByte : JitOps.LoadSignedHalf;
                    EmitMemory(address, cond, true, () => EmitLoad(kind, rd, rn, address, offset, up, pre, writeBack));
                }
                else
                {
                    EmitMemory(address, cond, false, () => EmitStore(JitOps.StoreHalf,
                        rd == 15 ? Const(address + 12) : Reg(rd, address), rn, address, offset, up, pre, writeBack));
                }
                return true;
            }

            private bool TryDataProcessing(uint address, uint op, uint cond)
            {
                bool immediate = (op & (1u << 25)) != 0;
                if (!immediate && (op & (1u << 4)) != 0)
                    return false; // register-specified shift: extra cycle and PC + 12, leave to the handler
                int opcode = (int)(op >> 21) & 0xF;
                bool s = (op & (1u << 20)) != 0;
                int rn = (int)(op >> 16) & 0xF;
                int rd = (int)(op >> 12) & 0xF;
                bool compare = opcode is >= 8 and <= 11;
                if (rd == 15 && !compare)
                    return false; // writes PC
                bool logical = opcode is 0 or 1 or 8 or 9 or >= 12;
                bool needCarry = s && logical;
                var method = s ? DataOpsS[opcode] : DataOps[opcode];
                if (method == null)
                    return false;

                var skip = _il.DefineLabel();
                EmitCondition(cond, skip);

                // Shifter carry (only the flag-setting logical ops use it).
                uint immValue = 0;
                if (immediate)
                {
                    int rotate = (int)((op >> 8) & 0xF) * 2;
                    immValue = BitOperations.RotateRight(op & 0xFF, rotate);
                    if (needCarry)
                    {
                        if (rotate != 0)
                            _il.Emit((immValue >> 31) != 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        else
                        {
                            _il.Emit(OpCodes.Ldarg, ArgCpu);
                            _il.Emit(OpCodes.Call, GetFlagC);
                        }
                        _il.Emit(OpCodes.Stloc, _carry);
                    }
                }
                else if (needCarry)
                {
                    _il.Emit(OpCodes.Ldarg, ArgCpu);
                    _il.Emit(OpCodes.Call, GetFlagC);
                    _il.Emit(OpCodes.Stloc, _carry);
                }

                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, rd);
                EmitReg(rn, address);
                if (immediate)
                {
                    _il.Emit(OpCodes.Ldc_I4, unchecked((int)immValue));
                }
                else
                {
                    int rm = (int)(op & 0xF);
                    int type = (int)(op >> 5) & 3;
                    int amount = (int)(op >> 7) & 0x1F;
                    if (needCarry)
                    {
                        EmitReg(rm, address);
                        _il.Emit(OpCodes.Ldc_I4, type);
                        _il.Emit(OpCodes.Ldc_I4, amount);
                        _il.Emit(OpCodes.Ldloca, _carry);
                        _il.Emit(OpCodes.Call, ShiftImmediateCarry);
                    }
                    else if (type == Alu.SHIFT_LSL && amount == 0)
                    {
                        EmitReg(rm, address);
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        EmitReg(rm, address);
                        _il.Emit(OpCodes.Ldc_I4, type);
                        _il.Emit(OpCodes.Ldc_I4, amount);
                        _il.Emit(OpCodes.Call, Op("ShiftImm"));
                    }
                }
                if (needCarry)
                    _il.Emit(OpCodes.Ldloc, _carry);
                _il.Emit(OpCodes.Call, method);

                _il.MarkLabel(skip);
                EmitCycles(1);
                EmitTimeCheck(address);
                return true;
            }

            private void EmitBranchWithLink(uint address, uint op, uint cond)
            {
                var skip = _il.DefineLabel();
                EmitCondition(cond, skip);
                if ((op & (1u << 24)) != 0)
                    EmitSet(14, address + 4);
                int offset = ((int)(op << 8)) >> 6;
                EmitTakenBranch(address, (uint)(address + 8 + offset) & ~3u);
                _il.MarkLabel(skip);
                if (cond != 0xE)
                {
                    EmitCycles(1);
                    EmitTimeCheck(address);
                }
            }

            private void EmitSet(int rd, uint value)
            {
                _il.Emit(OpCodes.Ldarg, ArgCpu);
                _il.Emit(OpCodes.Ldc_I4, rd);
                _il.Emit(OpCodes.Ldc_I4, unchecked((int)value));
                _il.Emit(OpCodes.Call, Op("Set"));
            }

            // ── Thumb ─────────────────────────────────────────────────────────

            private bool TryThumb(uint address, uint op)
            {
                switch (op >> 13)
                {
                    case 0: // formats 1 and 2
                    {
                        int rd = (int)(op & 7);
                        int rs = (int)(op >> 3) & 7;
                        if (((op >> 11) & 3) != 3)
                        {
                            _il.Emit(OpCodes.Ldarg, ArgCpu);
                            _il.Emit(OpCodes.Ldc_I4, rd);
                            EmitReg(rs, address);
                            _il.Emit(OpCodes.Ldc_I4, (int)(op >> 11) & 3);
                            _il.Emit(OpCodes.Ldc_I4, (int)(op >> 6) & 0x1F);
                            _il.Emit(OpCodes.Call, Op("ThumbShift"));
                        }
                        else
                        {
                            uint field = (op >> 6) & 7;
                            _il.Emit(OpCodes.Ldarg, ArgCpu);
                            _il.Emit(OpCodes.Ldc_I4, rd);
                            EmitReg(rs, address);
                            if ((op & (1u << 10)) != 0)
                                _il.Emit(OpCodes.Ldc_I4, (int)field);
                            else
                                EmitReg((int)field, address);
                            _il.Emit(OpCodes.Call, Op((op & (1u << 9)) != 0 ? "Subs" : "Adds"));
                        }
                        break;
                    }

                    case 1: // format 3: MOV/CMP/ADD/SUB Rd, #imm8
                    {
                        int rd = (int)(op >> 8) & 7;
                        uint imm = op & 0xFF;
                        uint kind = (op >> 11) & 3;
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        _il.Emit(OpCodes.Ldc_I4, rd);
                        if (kind == 0)
                        {
                            _il.Emit(OpCodes.Ldc_I4, (int)imm);
                            _il.Emit(OpCodes.Call, Op("ThumbMov"));
                        }
                        else
                        {
                            EmitReg(rd, address);
                            _il.Emit(OpCodes.Ldc_I4, (int)imm);
                            _il.Emit(OpCodes.Call, Op(kind == 1 ? "Cmp" : kind == 2 ? "Adds" : "Subs"));
                        }
                        break;
                    }

                    case 2 when (op & 0xFC00) == 0x4000: // format 4: ALU
                    {
                        int aluOp = (int)(op >> 6) & 0xF;
                        int rd = (int)(op & 7);
                        int rs = (int)(op >> 3) & 7;
                        if (aluOp == 0xD)
                            return false; // MUL: data-dependent cycles
                        int shiftType = aluOp switch { 0x2 => Alu.SHIFT_LSL, 0x3 => Alu.SHIFT_LSR, 0x4 => Alu.SHIFT_ASR, 0x7 => Alu.SHIFT_ROR, _ => -1 };
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        _il.Emit(OpCodes.Ldc_I4, rd);
                        EmitReg(rd, address);
                        EmitReg(rs, address);
                        if (shiftType >= 0)
                        {
                            _il.Emit(OpCodes.Ldc_I4, shiftType);
                            _il.Emit(OpCodes.Call, Op("TShiftReg"));
                            EmitCycles(2);
                            EmitTimeCheck(address);
                            return true;
                        }
                        _il.Emit(OpCodes.Call, ThumbAluOps[aluOp]);
                        break;
                    }

                    case 2 when (op & 0xFC00) == 0x4400: // format 5: hi-register ADD/CMP/MOV
                    {
                        int rd = (int)(op & 7) | (int)((op >> 4) & 8);
                        int rs = (int)((op >> 3) & 0xF);
                        uint kind = (op >> 8) & 3;
                        if (kind == 3 || (rd == 15 && kind != 1))
                            return false; // BX/BLX, PC writes
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        _il.Emit(OpCodes.Ldc_I4, rd);
                        EmitReg(rd, address);
                        EmitReg(rs, address);
                        _il.Emit(OpCodes.Call, Op(kind == 0 ? "Add" : kind == 1 ? "Cmp" : "Mov"));
                        break;
                    }

                    case 2 when (op & 0xF800) == 0x4800: // format 6: LDR Rd, [PC, #imm]
                    {
                        int rd = (int)(op >> 8) & 7;
                        uint target = ((address + 4) & ~2u) + ((op & 0xFF) << 2);
                        EmitMemory(address, 0xE, true, () => EmitLoad(JitOps.LoadWord, rd, 15, address, Const(target - (address + 4)), true, true, false));
                        return true;
                    }

                    case 2 when (op & 0xF000) == 0x5000: // formats 7 and 8: register offset
                    {
                        int rd = (int)(op & 7);
                        int rb = (int)(op >> 3) & 7;
                        int ro = (int)(op >> 6) & 7;
                        uint kind = (op >> 10) & 3;
                        if ((op & (1u << 9)) == 0)
                        {
                            if (kind < 2)
                                EmitMemory(address, 0xE, false, () => EmitStore(kind == 0 ? JitOps.StoreWord : JitOps.StoreByte, Reg(rd, address), rb, address, Reg(ro, address), true, true, false));
                            else
                                EmitMemory(address, 0xE, true, () => EmitLoad(kind == 2 ? JitOps.LoadWord : JitOps.LoadByte, rd, rb, address, Reg(ro, address), true, true, false));
                        }
                        else if (kind == 0)
                        {
                            EmitMemory(address, 0xE, false, () => EmitStore(JitOps.StoreHalf, Reg(rd, address), rb, address, Reg(ro, address), true, true, false));
                        }
                        else
                        {
                            int load = kind == 1 ? JitOps.LoadSignedByte : kind == 2 ? JitOps.LoadHalf : JitOps.LoadSignedHalf;
                            EmitMemory(address, 0xE, true, () => EmitLoad(load, rd, rb, address, Reg(ro, address), true, true, false));
                        }
                        return true;
                    }

                    case 3: // format 9: immediate offset
                    {
                        int rd = (int)(op & 7);
                        int rb = (int)(op >> 3) & 7;
                        uint imm = (op >> 6) & 0x1F;
                        switch ((op >> 11) & 3)
                        {
                            case 0: EmitMemory(address, 0xE, false, () => EmitStore(JitOps.StoreWord, Reg(rd, address), rb, address, Const(imm << 2), true, true, false)); break;
                            case 1: EmitMemory(address, 0xE, true, () => EmitLoad(JitOps.LoadWord, rd, rb, address, Const(imm << 2), true, true, false)); break;
                            case 2: EmitMemory(address, 0xE, false, () => EmitStore(JitOps.StoreByte, Reg(rd, address), rb, address, Const(imm), true, true, false)); break;
                            default: EmitMemory(address, 0xE, true, () => EmitLoad(JitOps.LoadByte, rd, rb, address, Const(imm), true, true, false)); break;
                        }
                        return true;
                    }

                    case 4: // formats 10 and 11: halfword immediate, SP-relative
                    {
                        bool load = (op & (1u << 11)) != 0;
                        if ((op & 0x1000) == 0)
                        {
                            int rd = (int)(op & 7);
                            int rb = (int)(op >> 3) & 7;
                            uint offset = ((op >> 6) & 0x1F) << 1;
                            if (load) EmitMemory(address, 0xE, true, () => EmitLoad(JitOps.LoadHalf, rd, rb, address, Const(offset), true, true, false));
                            else EmitMemory(address, 0xE, false, () => EmitStore(JitOps.StoreHalf, Reg(rd, address), rb, address, Const(offset), true, true, false));
                        }
                        else
                        {
                            int rd = (int)(op >> 8) & 7;
                            uint offset = (op & 0xFF) << 2;
                            if (load) EmitMemory(address, 0xE, true, () => EmitLoad(JitOps.LoadWord, rd, 13, address, Const(offset), true, true, false));
                            else EmitMemory(address, 0xE, false, () => EmitStore(JitOps.StoreWord, Reg(rd, address), 13, address, Const(offset), true, true, false));
                        }
                        return true;
                    }

                    case 5 when (op & 0xF000) == 0xA000: // format 12: ADD Rd, PC/SP, #imm
                    {
                        int rd = (int)(op >> 8) & 7;
                        uint offset = (op & 0xFF) << 2;
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        _il.Emit(OpCodes.Ldc_I4, rd);
                        if ((op & (1u << 11)) != 0)
                        {
                            EmitReg(13, address);
                            _il.Emit(OpCodes.Ldc_I4, (int)offset);
                            _il.Emit(OpCodes.Add);
                        }
                        else
                        {
                            _il.Emit(OpCodes.Ldc_I4, unchecked((int)(((address + 4) & ~2u) + offset)));
                        }
                        _il.Emit(OpCodes.Call, Op("Set"));
                        break;
                    }

                    case 5 when (op & 0xFF00) == 0xB000: // format 13: ADD SP, #±imm
                    {
                        uint offset = (op & 0x7F) << 2;
                        _il.Emit(OpCodes.Ldarg, ArgCpu);
                        _il.Emit(OpCodes.Ldc_I4, 13);
                        EmitReg(13, address);
                        _il.Emit(OpCodes.Ldc_I4, (int)offset);
                        _il.Emit((op & 0x80) != 0 ? OpCodes.Sub : OpCodes.Add);
                        _il.Emit(OpCodes.Call, Op("Set"));
                        break;
                    }

                    case 6 when (op & 0xF000) == 0xD000: // format 16: conditional branch
                    {
                        uint cond = (op >> 8) & 0xF;
                        if (cond >= 0xE)
                            return false; // undefined / SWI
                        var skip = _il.DefineLabel();
                        EmitCondition(cond, skip);
                        int offset = (sbyte)(op & 0xFF) * 2;
                        EmitTakenBranch(address, (uint)(address + 4 + offset) & ~1u);
                        _il.MarkLabel(skip);
                        EmitCycles(1);
                        EmitTimeCheck(address);
                        return true;
                    }

                    case 7 when (op & 0xF800) == 0xE000: // format 18: unconditional branch
                    {
                        int offset = ((int)(op << 21)) >> 20;
                        EmitTakenBranch(address, (uint)(address + 4 + offset) & ~1u);
                        return true;
                    }

                    default:
                        return false;
                }

                EmitCycles(1);
                EmitTimeCheck(address);
                return true;
            }
        }
    }
}
