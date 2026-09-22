namespace ServerPackets
{
    public sealed class ValorRow
    {
        public string Name = "";
        public ushort Level;
        public byte Team;
        public int Personal, Kills, Deaths, Bonus, Honor;
    }

    public sealed class ValorStatus : Packet
    {
        public override short Index => (short)ServerPacketIds.ValorStatus;
        public bool Active;
        public int RedScore, BlueScore, Seconds;
        public byte Winner;
        // Each monument reports damage dealt and health remaining, plus the attacking team.
        public int SunDamage, SunHealth, MoonDamage, MoonHealth, LightningDamage, LightningHealth;
        public byte SunAttacker, MoonAttacker, LightningAttacker;
        public List<ValorRow> Rows = new List<ValorRow>();

        protected override void ReadPacket(BinaryReader reader)
        {
            Active = reader.ReadBoolean();
            RedScore = reader.ReadInt32(); BlueScore = reader.ReadInt32(); Seconds = reader.ReadInt32();
            Winner = reader.ReadByte();
            SunDamage = reader.ReadInt32(); SunHealth = reader.ReadInt32();
            MoonDamage = reader.ReadInt32(); MoonHealth = reader.ReadInt32();
            LightningDamage = reader.ReadInt32(); LightningHealth = reader.ReadInt32();
            SunAttacker = reader.ReadByte(); MoonAttacker = reader.ReadByte(); LightningAttacker = reader.ReadByte();
            int count = reader.ReadInt32();
            if (count < 0 || count > 100) throw new InvalidDataException("Invalid Valor row count.");
            Rows.Clear();
            for (int i = 0; i < count; i++)
                Rows.Add(new ValorRow { Name = reader.ReadString(), Level = reader.ReadUInt16(), Team = reader.ReadByte(),
                    Personal = reader.ReadInt32(), Kills = reader.ReadInt32(), Deaths = reader.ReadInt32(),
                    Bonus = reader.ReadInt32(), Honor = reader.ReadInt32() });
        }

        protected override void WritePacket(BinaryWriter writer)
        {
            writer.Write(Active);
            writer.Write(RedScore); writer.Write(BlueScore); writer.Write(Seconds);
            writer.Write(Winner);
            writer.Write(SunDamage); writer.Write(SunHealth);
            writer.Write(MoonDamage); writer.Write(MoonHealth);
            writer.Write(LightningDamage); writer.Write(LightningHealth);
            writer.Write(SunAttacker); writer.Write(MoonAttacker); writer.Write(LightningAttacker);
            if (Rows.Count > 100) throw new InvalidDataException("Too many Valor rows.");
            writer.Write(Rows.Count);
            foreach (var row in Rows)
            {
                writer.Write(row.Name); writer.Write(row.Level); writer.Write(row.Team);
                writer.Write(row.Personal); writer.Write(row.Kills); writer.Write(row.Deaths);
                writer.Write(row.Bonus); writer.Write(row.Honor);
            }
        }
    }
}
