---
name: nds-implement-instruction
description: Add or fix an ARM or Thumb instruction in the Px86.NintendoDS CPU interpreter (ARMv4T ARM7 / ARMv5TE ARM9) — decode-table wiring, execution, flags, cycles, disassembly and tests. Use when a game hits UnknownInstruction, a CPU test fails, or an opcode behaves differently from GBATEK.
argument-hint: [mnemonic or opcode hex]
---

# Implement or fix an ARM/Thumb instruction

The interpreter lives in `Px86.NintendoDS.Common/Cpu/`. Both cores share one `ArmCpu`, and `cpu.IsArmV5` selects the ARM9 behaviour.

## Steps

1. **Pin down the encoding.** Get the exact bits from llvm-mc instead of encoding by hand:
   ```bash
   echo "smlabb r0, r1, r2, r3" | llvm-mc -triple=armv5te -show-encoding
   echo "ldrsh r5, [r1, r4]"   | llvm-mc -triple=thumbv5te -show-encoding
   ```
   Read the GBATEK section for the opcode ("ARM Opcodes: …" / "THUMB Opcodes: …"). The text copy can be grepped:
   `curl -sSL https://problemkaputt.de/gbatek.txt -o /tmp/gbatek.txt`.

2. **Find the decode slot.**
   - ARM: index = `((op >> 16) & 0xFF0) | ((op >> 4) & 0xF)` → `ArmInstructionTable.Classify`. Condition 1111 goes to
     `ExecuteUnconditional` (ARMv5 BLX imm, PLD).
   - Thumb: index = `op >> 6` → `ThumbInstructionTable.Classify`.
   Check that the slot returns the right handler. Misclassification is a common bug (e.g. the misc space where opcode is
   TST/TEQ/CMP/CMN with S = 0).

3. **Implement in the matching handler class** (`DataProcessing`, `Multiply`, `SingleDataTransfer`, `BlockDataTransfer`,
   `Branches`, `PsrTransfer`, `Miscellaneous`, `Coprocessor`, or the `Thumb*` classes). Rules:
   - Never assign `R[15]`. Use `cpu.Branch` (same state), `cpu.BranchExchange` (bit 0 picks Thumb) or `cpu.LoadPc`
     (interworks on v5 only).
   - Use `Alu.Add/Subtract/AddWithCarry/SubtractWithCarry` and `cpu.SetNZ/SetNZC/SetNZCV` for flags.
   - Misaligned loads: `Alu.ReadWordRotated`, `Alu.ReadHalfword`, `Alu.ReadSignedHalfword` (these already handle v4/v5 differences).
   - ARMv5-only opcodes on ARMv4 → `ArmInstructionTable.Undefined(cpu, op)`.
   - Charge memory accesses with `cpu.AddCycles(n)` (≈1 per access, +1 for a register-specified shift, `Multiply.MultiplierCycles`).

4. **Disassembly.** Update `ArmDisassembler` / `ThumbDisassembler` to print the llvm-mc mnemonic (with our `#0x..` immediates).

5. **Tests** in `Px86.NintendoDS.TestProject/Cpu/`:
   - An execution test with `CpuTestHarness.CreateArm/CreateThumb(arch, opcodes…)` + `Run(cpu, n)`, asserting
     registers, flags and memory (`CpuTestHarness.Memory(cpu)`). Paste the llvm-mc output as `0x…u, // mnemonic` lines.
   - When v4 and v5 differ, test both architectures.
   - A `[DataRow]` in `TestDisassembler`.

6. Run `dotnet test Px86.NintendoDS.TestProject` and make sure every test passes.

## v4T vs v5TE checklist
BLX (imm/reg), CLZ, BKPT, QADD/QSUB/QDADD/QDSUB, SMLAxy/SMLAWy/SMULWy/SMLALxy/SMULxy, LDRD/STRD, PLD,
LDR/LDM/POP to PC interworking, misaligned LDRH/LDRSH, LDM/STM with Rn in Rlist, empty Rlist, Q flag in MSR masks.
