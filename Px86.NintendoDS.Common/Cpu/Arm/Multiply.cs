namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM multiply instructions.
    ///
    ///   MUL/MLA      cond 000000 A S Rd Rn Rs 1001 Rm        Rd = Rm*Rs (+Rn)
    ///   UMULL/UMLAL  cond 00001 U A S RdHi RdLo Rs 1001 Rm   RdHi:RdLo = Rm*Rs (+RdHi:RdLo)
    ///   SMULL/SMLAL  (U = 1 selects signed)
    ///
    ///   ARMv5TE signed halfword multiplies (cond 00010 op 0 … 1yx0 …):
    ///     op 00  SMLAxy   Rd = Rm.x * Rs.y + Rn            (sets Q on accumulate overflow)
    ///     op 01  SMLAWy   Rd = (Rm * Rs.y) &gt;&gt; 16 + Rn      (x = 0)
    ///            SMULWy   Rd = (Rm * Rs.y) &gt;&gt; 16           (x = 1)
    ///     op 10  SMLALxy  RdHi:RdLo += Rm.x * Rs.y
    ///     op 11  SMULxy   Rd = Rm.x * Rs.y
    ///
    /// Flags (S = 1): N and Z from the result. C is left unchanged (it is
    /// "destroyed" on ARMv4, which no software relies on). V is unchanged.
    ///
    /// Reference: GBATEK "ARM Opcodes: Multiply and Multiply-Accumulate (MUL, MLA)".
    /// </summary>
    public static class Multiply
    {
        public static void MultiplyAccumulate(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 16) & 0xF;
            int rn = (int)(op >> 12) & 0xF;
            int rs = (int)(op >> 8) & 0xF;
            int rm = (int)(op & 0xF);

            uint result = cpu.R[rm] * cpu.R[rs];
            if ((op & (1u << 21)) != 0)
            {
                result += cpu.R[rn];
                cpu.AddCycles(1);
            }

            cpu.R[rd] = result;
            if ((op & (1u << 20)) != 0)
                cpu.SetNZ(result);
            cpu.AddCycles(MultiplierCycles(cpu.R[rs]));
        }

        public static void MultiplyLong(ArmCpu cpu, uint op)
        {
            int rdHi = (int)(op >> 16) & 0xF;
            int rdLo = (int)(op >> 12) & 0xF;
            int rs = (int)(op >> 8) & 0xF;
            int rm = (int)(op & 0xF);
            bool signed = (op & (1u << 22)) != 0;

            ulong result = signed
                ? (ulong)((long)(int)cpu.R[rm] * (int)cpu.R[rs])
                : (ulong)cpu.R[rm] * cpu.R[rs];

            if ((op & (1u << 21)) != 0)
            {
                result += ((ulong)cpu.R[rdHi] << 32) | cpu.R[rdLo];
                cpu.AddCycles(1);
            }

            cpu.R[rdLo] = (uint)result;
            cpu.R[rdHi] = (uint)(result >> 32);

            if ((op & (1u << 20)) != 0)
                SetLongFlags(cpu, result);
            cpu.AddCycles(MultiplierCycles(cpu.R[rs]) + 1);
        }

        /// <summary>N from bit 63, Z from all 64 bits; C and V preserved.</summary>
        private static void SetLongFlags(ArmCpu cpu, ulong result)
        {
            uint n = (uint)(result >> 32) & ArmCpu.FLAG_N;
            uint z = result == 0 ? ArmCpu.FLAG_Z : 0;
            cpu.SetFlagsFromBits((cpu.Cpsr & 0x30000000u) | n | z);
        }

        public static void SignedHalfwordMultiply(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ArmInstructionTable.Undefined(cpu, op);
                return;
            }

            int rd = (int)(op >> 16) & 0xF;   // RdHi for SMLALxy
            int rn = (int)(op >> 12) & 0xF;   // RdLo for SMLALxy
            int rs = (int)(op >> 8) & 0xF;
            int rm = (int)(op & 0xF);
            bool x = (op & (1u << 5)) != 0;
            bool y = (op & (1u << 6)) != 0;

            int rmHalf = x ? (short)(cpu.R[rm] >> 16) : (short)cpu.R[rm];
            int rsHalf = y ? (short)(cpu.R[rs] >> 16) : (short)cpu.R[rs];

            switch ((op >> 21) & 3)
            {
                case 0: // SMLAxy
                {
                    int product = rmHalf * rsHalf;
                    long sum = (long)product + (int)cpu.R[rn];
                    if (sum > int.MaxValue || sum < int.MinValue)
                        cpu.SetQ();
                    cpu.R[rd] = (uint)(int)sum;
                    break;
                }
                case 1: // SMLAWy / SMULWy
                {
                    int product = (int)(((long)(int)cpu.R[rm] * rsHalf) >> 16);
                    if (!x)
                    {
                        long sum = (long)product + (int)cpu.R[rn];
                        if (sum > int.MaxValue || sum < int.MinValue)
                            cpu.SetQ();
                        cpu.R[rd] = (uint)(int)sum;
                    }
                    else
                    {
                        cpu.R[rd] = (uint)product;
                    }
                    break;
                }
                case 2: // SMLALxy
                {
                    long acc = (long)(((ulong)cpu.R[rd] << 32) | cpu.R[rn]);
                    acc += (long)rmHalf * rsHalf;
                    cpu.R[rn] = (uint)acc;
                    cpu.R[rd] = (uint)((ulong)acc >> 32);
                    cpu.AddCycles(1);
                    break;
                }
                default: // SMULxy
                    cpu.R[rd] = (uint)(rmHalf * rsHalf);
                    break;
            }
        }

        /// <summary>Early-terminating multiplier: 1–4 cycles depending on the magnitude of Rs.</summary>
        internal static int MultiplierCycles(uint rs)
        {
            if ((rs & 0xFFFFFF00u) == 0 || (rs & 0xFFFFFF00u) == 0xFFFFFF00u) return 1;
            if ((rs & 0xFFFF0000u) == 0 || (rs & 0xFFFF0000u) == 0xFFFF0000u) return 2;
            if ((rs & 0xFF000000u) == 0 || (rs & 0xFF000000u) == 0xFF000000u) return 3;
            return 4;
        }
    }
}
