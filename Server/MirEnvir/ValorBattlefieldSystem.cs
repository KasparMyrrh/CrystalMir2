using System.Drawing;
using Server.MirDatabase;
using Server.MirObjects;
using S = ServerPackets;

namespace Server.MirEnvir
{
    public sealed class ValorBattlefieldSystem
    {
        private enum Phase { Idle, Registration, Active, Settling }
        private sealed class Member
        {
            public PlayerObject Player;
            public ValorTeam Team;
            public AttackMode PreviousMode;
            public int ReturnMap;
            public Point ReturnLocation;
            public int Personal, Kills, Deaths;
            public long ReviveAt, PreviousBrownTime;
        }
        private sealed class ValorMonster : MonsterObject
        {
            private readonly int _monumentHealth;
            public ValorMonster(MonsterInfo info, int monumentHealth = 0) : base(info)
            {
                _monumentHealth = monumentHealth;
            }
            public override void RefreshAll()
            {
                base.RefreshAll();
                if (_monumentHealth > 0) Stats[Stat.HP] = _monumentHealth;
            }
            protected override void ProcessAI()
            {
                ProcessRegen();
                ProcessPoison();
            }
            public override int Pushed(MapObject pusher, MirDirection direction, int distance) => 0;
        }
        private sealed class Objective
        {
            public int Id;
            public Point Location;
            public MonsterObject Monster;
            public ValorTeam Owner;
            public long RespawnAt;
            public MapObject LastDamager;
        }

        private readonly object _sync = new object();
        private readonly List<PlayerObject> _applications = new List<PlayerObject>();
        private readonly Dictionary<PlayerObject, Member> _members = new Dictionary<PlayerObject, Member>();
        private readonly Objective[] _monuments = {
            new Objective { Id = 575, Location = new Point(201, 198) },
            new Objective { Id = 576, Location = new Point(77, 196) },
            new Objective { Id = 577, Location = new Point(324, 207) }
        };
        private readonly Objective[] _buffers = {
            new Objective { Id = 578, Location = new Point(339, 122) },
            new Objective { Id = 579, Location = new Point(64, 281) }
        };
        private readonly ValorHonorStore _honor = new ValorHonorStore();
        private ValorSettings _settings;
        private Map _map;
        private Phase _phase;
        private long _ends, _scoreAt, _statusAt, _settleAt;
        private int _redScore, _blueScore;
        private ValorTeam _winner;
        private bool _returning;
        private static Envir World => Envir.Main;
        private static MessageQueue MessageQueue => MessageQueue.Instance;
        private static readonly Point BlueSpawn = new Point(50, 42), RedSpawn = new Point(354, 360);
        private static Point Spawn(ValorTeam team) => team == ValorTeam.Red ? RedSpawn : BlueSpawn;

        public bool HasPlayer(PlayerObject player) { lock (_sync) return player != null && (_applications.Contains(player) || _members.ContainsKey(player)); }
        public bool Contains(PlayerObject player) { lock (_sync) return player != null && _members.ContainsKey(player); }
        public bool IsMap(Map map) { lock (_sync) return map != null && string.Equals(Path.GetFileNameWithoutExtension(map.Info.FileName),
            _settings?.MapFileName ?? "valor", StringComparison.OrdinalIgnoreCase); }
        public bool IsParticipant(PlayerObject player) { lock (_sync) return Contains(player) && player.Node != null && player.CurrentMap == _map; }
        public bool CanEnter(PlayerObject player, Map map) { lock (_sync) return !IsMap(map) || IsGMVisitor(player) ||
            ((_phase == Phase.Active || _phase == Phase.Settling) && Contains(player)); }
        private static bool IsGMVisitor(PlayerObject player) => player != null && player.IsGM;

        public bool TryGetRelationship(PlayerObject first, PlayerObject second, out bool hostile)
        {
            lock (_sync)
            {
                hostile = false;
                if (first == null || second == null || (!IsMap(first.CurrentMap) && !IsMap(second.CurrentMap))) return false;
                hostile = _phase == Phase.Active && IsParticipant(first) && IsParticipant(second)
                    && _members[first].Team != _members[second].Team;
                return true;
            }
        }

        public bool TryGetNameColour(PlayerObject player, out Color colour)
        {
            lock (_sync)
            {
                colour = Color.White;
                if (!IsParticipant(player)) return false;
                colour = _members[player].Team == ValorTeam.Red ? Color.Red : Color.Blue;
                return true;
            }
        }

        public bool TryGetPetRelationship(MonsterObject target, MapObject source, out bool canAttack)
        {
            lock (_sync)
            {
                canAttack = false;
                if (target.Master == null) return false;
                var first = Owner(target.Master);
                var second = Owner(source);
                if (!TryGetRelationship(first, second, out bool hostile)) return false;
                canAttack = hostile && target.CurrentMap == first.CurrentMap && source.CurrentMap == second.CurrentMap
                    && !target.InSafeZone && !source.InSafeZone;
                return true;
            }
        }

        private static PlayerObject Owner(MapObject source)
        {
            if (source is PlayerObject player) return player;
            if (source is HeroObject hero) return hero.Owner;
            if (source is MonsterObject pet && pet.Master != source) return Owner(pet.Master);
            return null;
        }

        private Objective Find(MonsterObject monster) => _monuments.Concat(_buffers).FirstOrDefault(o => o.Monster == monster);
        public bool IsObjective(MonsterObject monster) { lock (_sync) return monster != null && Find(monster) != null; }
        public bool CanAttackObjective(MonsterObject monster, MapObject source)
        {
            lock (_sync)
            {
                var objective = Find(monster);
                var player = Owner(source);
                if (_phase != Phase.Active || objective == null || player == null || !IsParticipant(player) || player.Dead) return false;
                return !_monuments.Contains(objective) || objective.Owner != _members[player].Team;
            }
        }

        public void RecordDamage(MonsterObject monster, MapObject source)
        {
            lock (_sync)
            {
                var objective = Find(monster);
                if (objective != null && CanAttackObjective(monster, source)) objective.LastDamager = source;
            }
        }

        public bool OnMonsterDeath(MonsterObject monster)
        {
            lock (_sync)
            {
                var objective = Find(monster);
                if (objective == null) return false;
                var killer = Owner(objective.LastDamager);
                bool valid = _phase == Phase.Active && killer != null && IsParticipant(killer) && !killer.Dead;
                if (_monuments.Contains(objective))
                {
                    if (valid)
                    {
                        objective.Owner = _members[killer].Team;
                        Announce($"{objective.Owner} captured {World.GetMonsterInfo(objective.Id).Name}!");
                    }
                    objective.RespawnAt = World.Time + 1000;
                }
                else
                {
                    // One buffer at each supplied spawn point; each group therefore has one member.
                    objective.RespawnAt = World.Time + 180000;
                    if (valid)
                    {
                        killer.AddBuff(BuffType.Valor, killer, _settings.BufferDurationSeconds * 1000,
                            new Stats { [Stat.MinDC] = _settings.BufferAttackBonus, [Stat.MaxDC] = _settings.BufferAttackBonus,
                                [Stat.MinMC] = _settings.BufferAttackBonus, [Stat.MaxMC] = _settings.BufferAttackBonus,
                                [Stat.MinSC] = _settings.BufferAttackBonus, [Stat.MaxSC] = _settings.BufferAttackBonus,
                                [Stat.MinAC] = _settings.BufferDefenceBonus, [Stat.MaxAC] = _settings.BufferDefenceBonus,
                                [Stat.MinMAC] = _settings.BufferDefenceBonus, [Stat.MaxMAC] = _settings.BufferDefenceBonus });
                        killer.ReceiveChat("Valor buffer acquired.", ChatType.System);
                    }
                }
                monster.HP = 0;
                monster.Dead = true;
                // Event objectives never execute normal XP/drop/quest/death-script handling.
                RemoveMonster(monster);
                objective.Monster = null;
                objective.LastDamager = null;
                return true;
            }
        }

        private bool Configure(PlayerObject requester)
        {
            try
            {
                _settings = ValorSettings.Load();
                _map = World.GetMapByNameAndInstance(_settings.MapFileName);
                if (_map == null || !_map.ValidPoint(BlueSpawn) || !_map.ValidPoint(RedSpawn)
                    || _monuments.Concat(_buffers).Any(o => !_map.ValidPoint(o.Location) || World.GetMonsterInfo(o.Id) == null
                        || World.GetMonsterInfo(o.Id).Stats[Stat.HP] <= 0))
                    throw new InvalidDataException("Configure valor map, valid spawn cells and monster database IDs 575–579.");
                if (_map.Info.NoFight || _map.Info.RequiredGroup || _map.Info.NoTeleport)
                    throw new InvalidDataException("Valor map must allow fighting and teleporting and must not require a group.");
                if (_map.Info.SafeZones.Any(z => _monuments.Concat(_buffers).Any(o => Functions.InRange(z.Location, o.Location, z.Size))))
                    throw new InvalidDataException("Keep objective locations outside safe zones.");
                _honor.Get(requester.Info.Index);
                return true;
            }
            catch (Exception ex)
            {
                requester.ReceiveChat("Valor could not open: " + ex.Message, ChatType.System);
                MessageQueue.Enqueue("Valor configuration: " + ex);
                return false;
            }
        }

        public void Open(PlayerObject requester, bool gmCommand = false)
        {
            lock (_sync)
            {
                if (requester == null || requester.Node == null || (gmCommand && !requester.IsGM)) return;
                if (!gmCommand && !AtRegistrationNpc(requester)) return;
                if (_phase != Phase.Idle) { requester.ReceiveChat("Valor registration or battle is already running.", ChatType.System); return; }
                if (!Configure(requester)) return;
                _phase = Phase.Registration;
                _ends = World.Time + 60000;
                Announce("BattleField has just begun, you may register at the NPC in BichonWall");
            }
        }

        private static bool AtRegistrationNpc(PlayerObject player)
        {
            var npc = NPCObject.Get(player.NPCObjectID);
            bool valid = npc != null && npc.CurrentMap == player.CurrentMap && npc.CurrentMap != null
                && string.Equals(npc.CurrentMap.Info.FileName, "0", StringComparison.OrdinalIgnoreCase)
                && npc.CurrentLocation == new Point(327, 258)
                && Functions.MaxDistance(player.CurrentLocation, npc.CurrentLocation) <= Globals.DataRange;
            if (!valid) player.ReceiveChat("Speak to the Valor NPC in Bichon at 327,258.", ChatType.System);
            return valid;
        }

        public void Register(PlayerObject player)
        {
            lock (_sync)
            {
                if (player == null || player.Node == null || !AtRegistrationNpc(player)) return;
                if (_phase != Phase.Registration || World.Time >= _ends)
                { player.ReceiveChat("Valor registration is closed. Open a new event through this NPC.", ChatType.System); return; }
                if (player.Dead || Contains(player) || _applications.Contains(player))
                { player.ReceiveChat("You cannot register twice or while dead.", ChatType.System); return; }
                if (_applications.Count >= _settings.MaximumPlayers)
                { player.ReceiveChat("Valor is full.", ChatType.System); return; }
                _applications.Add(player);
                player.SetTimer("ValorRegistration", (int)Math.Max(1, (_ends - World.Time + 999) / 1000), 1);
                player.ReceiveChat("Registered for Battlefield of Valor.", ChatType.System);
            }
        }

        public void ShowHonor(PlayerObject player)
        {
            lock (_sync)
            {
                if (player == null) return;
                try { player.ReceiveChat($"Valor Honor: {_honor.Get(player.Info.Index):N0} / 200,000", ChatType.System); }
                catch (Exception ex) { player.ReceiveChat("Valor honor unavailable: " + ex.Message, ChatType.System); }
            }
        }

        public void Exchange(PlayerObject player, int rewardIndex)
        {
            lock (_sync)
            {
                if (player == null || player.Dead || !AtRegistrationNpc(player)) return;
                try
                {
                    var settings = ValorSettings.Load();
                    if (rewardIndex < 0 || rewardIndex >= settings.Rewards.Count)
                    { player.ReceiveChat("That Valor reward has not been configured.", ChatType.System); return; }
                    var reward = settings.Rewards[rewardIndex];
                    var info = World.GetItemInfo(reward.Item);
                    var item = info == null ? null : World.CreateFreshItem(info);
                    if (item == null || !player.CanGainItem(item))
                    { player.ReceiveChat("Reward unavailable, or insufficient inventory space/weight.", ChatType.System); return; }
                    int balance = _honor.Get(player.Info.Index);
                    if (balance < reward.HonorCost)
                    { player.ReceiveChat($"This reward costs {reward.HonorCost:N0} Honor.", ChatType.System); return; }
                    _honor.SetMany(new Dictionary<int, int> { [player.Info.Index] = balance - reward.HonorCost });
                    player.GainItem(item);
                    ShowHonor(player);
                }
                catch (Exception ex) { player.ReceiveChat("Valor reward unavailable: " + ex.Message, ChatType.System); MessageQueue.Enqueue("Valor exchange: " + ex); }
            }
        }

        public bool RouteNormalChat(PlayerObject player, Packet packet)
        {
            lock (_sync)
            {
                if (!IsMap(player.CurrentMap)) return false;
                if (!IsParticipant(player)) return true;
                foreach (var member in _members.Values)
                    if (IsParticipant(member.Player) && member.Team == _members[player].Team
                        && Functions.MaxDistance(player.CurrentLocation, member.Player.CurrentLocation) <= Globals.DataRange)
                        member.Player.Enqueue(packet);
                return true;
            }
        }

        public void OnPlayerDeath(PlayerObject player)
        {
            lock (_sync)
            {
                if (!IsParticipant(player)) return;
                var member = _members[player];
                member.Deaths++;
                member.ReviveAt = World.Time + 2000;
                var killer = Owner(player.LastHitter);
                if (killer != null && TryGetRelationship(player, killer, out bool hostile) && hostile)
                    _members[killer].Kills++;
            }
        }

        public void OnMapChanged(PlayerObject player)
        {
            lock (_sync)
            {
                if (!_returning && Contains(player) && player.CurrentMap != _map) Leave(player, false);
            }
        }

        public void Leave(PlayerObject player, bool returnHome = true)
        {
            lock (_sync)
            {
                if (player == null) return;
                if (_applications.Remove(player)) player.ExpireTimer("ValorRegistration");
                if (!_members.TryGetValue(player, out var member)) return;
                // Settlement retries retain members so honor cannot be silently discarded on disk failure.
                try
                {
                    _honor.SetMany(new Dictionary<int, int> { [player.Info.Index] = ValorRules.AddHonor(
                        _honor.Get(player.Info.Index), member.Personal) });
                }
                catch (Exception ex) { MessageQueue.Enqueue("Valor leave honor: " + ex); return; }
                _members.Remove(player);
                Restore(member, returnHome);
                player.Enqueue(new S.ValorStatus());
                player.ReceiveChat($"Left Valor. Earned {member.Personal} personal Honor before the cap.", ChatType.System);
            }
        }

        private void Restore(Member member, bool returnHome)
        {
            var player = member.Player;
            player.AMode = member.PreviousMode;
            player.BrownTime = member.PreviousBrownTime;
            player.RemoveBuff(BuffType.Valor);
            player.ExpireTimer("ValorRegistration");
            player.Enqueue(new S.ChangeAMode { Mode = player.AMode });
            if (returnHome && player.Node != null)
            {
                if (player.Dead) player.Revive(player.Stats[Stat.HP], true);
                var map = World.GetMap(member.ReturnMap);
                var location = member.ReturnLocation;
                if (map == null || !map.ValidPoint(location))
                { map = World.GetMap(player.BindMapIndex); location = player.BindLocation; }
                if (map != null && map.ValidPoint(location))
                {
                    _returning = true;
                    try { player.Teleport(map, location); }
                    finally { _returning = false; }
                }
            }
            player.RefreshNameColour();
        }

        public void Process()
        {
            lock (_sync)
            {
                if (_phase == Phase.Registration && World.Time >= _ends) Start();
                if (_phase == Phase.Settling) { if (World.Time >= _settleAt) Settle(); return; }
                if (_phase != Phase.Active) return;
                foreach (var member in _members.Values.ToList())
                {
                    var player = member.Player;
                    if (player.Node == null || player.CurrentMap != _map) { Leave(player, false); continue; }
                    if (player.Dead && member.ReviveAt > 0 && World.Time >= member.ReviveAt)
                    {
                        player.Revive(player.Stats[Stat.HP], true);
                        player.Teleport(_map, Spawn(member.Team));
                        member.ReviveAt = 0;
                    }
                }
                if (World.Time >= _ends || !_members.Values.Any(m => m.Team == ValorTeam.Red)
                    || !_members.Values.Any(m => m.Team == ValorTeam.Blue))
                { Finish(); return; }
                foreach (var objective in _monuments.Concat(_buffers))
                {
                    if (objective.Monster == null && World.Time >= objective.RespawnAt && !SpawnObjective(objective))
                        objective.RespawnAt = World.Time + 1000;
                }
                if (World.Time >= _scoreAt)
                {
                    // One tick after a server stall; never award a backlog using current ownership.
                    _scoreAt = World.Time + _settings.ScoreIntervalSeconds * 1000;
                    _redScore += Ownership(ValorTeam.Red);
                    _blueScore += Ownership(ValorTeam.Blue);
                    foreach (var member in _members.Values)
                    {
                        if (member.Player.Dead || !IsParticipant(member.Player)) continue;
                        if (_monuments.Any(o => o.Owner == member.Team && Functions.MaxDistance(o.Location,
                            member.Player.CurrentLocation) <= _settings.MonumentRadius))
                        {
                            member.Personal += _settings.PersonalPointsPerTick;
                            if (member.Team == ValorTeam.Red) _redScore += _settings.PersonalPointsPerTick;
                            else _blueScore += _settings.PersonalPointsPerTick;
                        }
                    }
                    if (_redScore >= 7500 || _blueScore >= 7500) { Finish(); return; }
                }
                if (World.Time >= _statusAt)
                { _statusAt = World.Time + 1000; SendStatus(true); }
            }
        }

        private int Ownership(ValorTeam team) => ValorRules.OwnershipPoints(
            _monuments[0].Owner == team, _monuments[1].Owner == team, _monuments[2].Owner == team);

        private bool SpawnObjective(Objective objective)
        {
            var monster = new ValorMonster(World.GetMonsterInfo(objective.Id), MonumentHealth(objective));
            if (monster == null || !monster.Spawn(_map, objective.Location)) return false;
            objective.Monster = monster;
            objective.LastDamager = null;
            monster.Target = null;
            if (_monuments.Contains(objective))
            {
                monster.NameColour = objective.Owner == ValorTeam.Red ? Color.Red
                    : objective.Owner == ValorTeam.Blue ? Color.Blue : Color.White;
                monster.Broadcast(new S.ObjectColourChanged { ObjectID = monster.ObjectID, NameColour = monster.NameColour });
            }
            return true;
        }

        private void Start()
        {
            _applications.RemoveAll(p => p.Node == null || p.Dead || IsMap(p.CurrentMap));
            if (_applications.Count < 2) { Cancel("Valor cancelled: fewer than two eligible players registered."); return; }
            foreach (var o in _monuments.Concat(_buffers))
                if (!SpawnObjective(o)) { Cancel("Valor cancelled: an objective could not spawn."); return; }
            _phase = Phase.Active;
            _ends = World.Time + 1200000;
            _scoreAt = World.Time + _settings.ScoreIntervalSeconds * 1000;
            int redLevels = 0, blueLevels = 0, redCount = 0, blueCount = 0;
            foreach (var player in _applications.OrderByDescending(p => p.Level).ThenBy(p => p.Name))
            {
                var team = redCount < blueCount || (redCount == blueCount && redLevels <= blueLevels) ? ValorTeam.Red : ValorTeam.Blue;
                var member = new Member { Player = player, Team = team, PreviousMode = player.AMode, PreviousBrownTime = player.BrownTime,
                    ReturnMap = player.BindMapIndex, ReturnLocation = player.BindLocation };
                _members.Add(player, member);
                if (!player.Teleport(_map, Spawn(team)))
                { _members.Remove(player); Restore(member, false); continue; }
                if (team == ValorTeam.Red) { redCount++; redLevels += player.Level; }
                else { blueCount++; blueLevels += player.Level; }
                player.AMode = AttackMode.Valor;
                player.Enqueue(new S.ChangeAMode { Mode = player.AMode });
                player.RefreshNameColour();
                player.ExpireTimer("ValorRegistration");
                // The Valor HUD draws its own clock from ValorStatus.Seconds.
                // A generic timer here duplicates the clock at the bottom of the screen.
                player.ReceiveChat($"Valor: you are on the {team} team. Attack monuments to capture them!", ChatType.Announcement);
            }
            foreach (var applicant in _applications) applicant.ExpireTimer("ValorRegistration");
            _applications.Clear();
            if (redCount == 0 || blueCount == 0) { Cancel("Valor cancelled: both teams could not enter."); return; }
            Announce("Battlefield of Valor has started! First to 7,500 points wins; time limit 20 minutes.");
            SendStatus(true);
        }

        private void Finish()
        {
            _winner = ValorRules.Winner(_redScore, _blueScore);
            _phase = Phase.Settling;
            Settle();
        }

        private void Settle()
        {
            _settleAt = World.Time + 5000;
            try
            {
                var changes = new Dictionary<int, int>();
                foreach (var member in _members.Values)
                {
                    bool completed = IsParticipant(member.Player);
                    int award = ValorRules.HonorAward(member.Personal, completed, completed && member.Team == _winner,
                        _settings.CompletionHonor, _settings.VictoryHonor);
                    changes[member.Player.Info.Index] = ValorRules.AddHonor(_honor.Get(member.Player.Info.Index), award);
                }
                _honor.SetMany(changes);
            }
            catch (Exception ex)
            { MessageQueue.Enqueue("Valor settlement will retry: " + ex); return; }
            SendStatus(false);
            Announce(_winner == ValorTeam.None ? "Battlefield of Valor ended in a draw."
                : $"{_winner} won Battlefield of Valor! Red {_redScore:N0} / Blue {_blueScore:N0}.");
            var members = _members.Values.ToList();
            _members.Clear();
            foreach (var member in members)
            { if (member.Player.Node != null) { Restore(member, true); ShowHonor(member.Player); } }
            Reset();
        }

        private void SendStatus(bool active)
        {
            var rows = _members.Values.OrderByDescending(m => m.Personal).ThenByDescending(m => m.Kills)
                .ThenBy(m => m.Player.Name).Select(m => new S.ValorRow { Name = m.Player.Name, Level = m.Player.Level,
                    Team = (byte)m.Team, Personal = m.Personal, Kills = m.Kills, Deaths = m.Deaths,
                    Bonus = !active && IsParticipant(m.Player) ? _settings.CompletionHonor
                        + (m.Team == _winner ? _settings.VictoryHonor : 0) : 0,
                    Honor = _honor.Get(m.Player.Info.Index) }).ToList();
            var packet = new S.ValorStatus { Active = active, RedScore = _redScore, BlueScore = _blueScore,
                Seconds = active ? (int)Math.Max(0, (_ends - World.Time + 999) / 1000) : 0,
                Winner = (byte)_winner, Rows = rows };
            SetMonumentProgress(_monuments[0], out packet.SunDamage, out packet.SunHealth, out packet.SunAttacker);
            SetMonumentProgress(_monuments[1], out packet.MoonDamage, out packet.MoonHealth, out packet.MoonAttacker);
            SetMonumentProgress(_monuments[2], out packet.LightningDamage, out packet.LightningHealth, out packet.LightningAttacker);
            foreach (var member in _members.Values)
                if (member.Player.Node != null) member.Player.Enqueue(packet);
        }

        private void SetMonumentProgress(Objective objective, out int damage, out int health, out byte attacker)
        {
            int maximum = MonumentHealth(objective);
            if (maximum == 0) maximum = Math.Max(0, World.GetMonsterInfo(objective.Id).Stats[Stat.HP]);
            health = objective.Monster == null ? maximum : Math.Max(0, objective.Monster.HP);
            damage = Math.Max(0, maximum - health);
            var player = Owner(objective.LastDamager);
            attacker = player != null && _members.TryGetValue(player, out var member) ? (byte)member.Team : (byte)0;
        }

        // The battlefield monuments have event health independent of their database templates.
        // Buff monsters continue using their configured database health.
        private static int MonumentHealth(Objective objective) => objective.Id == 575 ? 300
            : objective.Id == 576 || objective.Id == 577 ? 100 : 0;

        private void Cancel(string reason)
        {
            Announce(reason);
            foreach (var player in _applications) player.ExpireTimer("ValorRegistration");
            var members = _members.Values.ToList();
            _members.Clear();
            foreach (var member in members) Restore(member, true);
            Reset();
        }

        private static void RemoveMonster(MonsterObject monster)
        {
            if (monster == null || monster.Node == null) return;
            monster.CurrentMap.RemoveObject(monster);
            monster.CurrentMap.MonsterCount--;
            World.MonsterCount--;
            monster.Despawn();
        }

        private void Reset()
        {
            foreach (var o in _monuments.Concat(_buffers))
            { RemoveMonster(o.Monster); o.Monster = null; o.Owner = ValorTeam.None; o.LastDamager = null; o.RespawnAt = 0; }
            _applications.Clear();
            _members.Clear();
            _redScore = _blueScore = 0;
            _winner = ValorTeam.None;
            _phase = Phase.Idle;
            _map = null;
        }

        private static void Announce(string text) => World.Broadcast(new S.Chat { Message = text, Type = ChatType.Announcement });
    }
}
