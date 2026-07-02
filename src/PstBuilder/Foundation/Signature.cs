namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: a little stamp proving a page is sitting in the spot it's supposed to be.
    /// Block/page signature computation. MS-PST 5.5. The 16-bit signature stored in a
    /// BLOCKTRAILER (<c>wSig</c>) or PAGETRAILER binds a block to its location and identity,
    /// derived from the file offset (IB) and the block identifier (BID).
    /// </summary>
    public static class Signature
    {
        /// <summary>
        /// Computes the 16-bit signature for a block or page at file offset <paramref name="ib"/>
        /// with identifier <paramref name="bid"/>. AMap/PMap/FMap/FPMap pages use a signature of 0
        /// and are excluded by the caller, not here.
        /// </summary>
        public static ushort Compute(ulong ib, ulong bid)
        {
            ulong x = ib ^ bid;
            return (ushort)((x >> 16) ^ x);
        }

        /// <summary>Computes the signature for the given BREF.</summary>
        public static ushort Compute(Bref bref) => Compute(bref.Ib, bref.Bid.Value);
    }
}
