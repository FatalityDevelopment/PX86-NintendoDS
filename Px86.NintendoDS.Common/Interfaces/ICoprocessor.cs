namespace Px86.NintendoDS.Common.Interfaces
{
    /// <summary>
    /// An ARM coprocessor reachable through MCR/MRC.
    ///
    /// On the DS only the ARM9 has a usable coprocessor: CP15, the system
    /// control coprocessor (cache/TCM/protection-unit configuration). The ARM7
    /// has no CP15; its MCR/MRC to p15 raise an undefined-instruction exception.
    /// </summary>
    public interface ICoprocessor
    {
        /// <summary>MRC p#, opc1, Rd, CRn, CRm, opc2.</summary>
        uint Read(int opcode1, int crn, int crm, int opcode2);

        /// <summary>MCR p#, opc1, Rd, CRn, CRm, opc2.</summary>
        void Write(int opcode1, int crn, int crm, int opcode2, uint value);
    }
}
