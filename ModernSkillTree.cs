using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("ModernSkillTree", "Kofu", "2.0.0")]
    public class ModernSkillTree : RustPlugin
    {
        #region Veri ve Config Modelleri
        
        // Config dosyasından okunacak Yetenek Modeli
        public class SkillDefinition
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string IconUrl { get; set; } // Modern ikonlar için URL
            public int MaxLevel { get; set; }
            public float BonusPerLevel { get; set; } // Seviye başı % kaç etki edecek
            public int RequiredCategoryLevel { get; set; } = 0; // Bu yeteneği açmak için ilgili kategoride toplam kaç puan harcanmış olmalı
        }

        // Config dosyasından okunacak Kategori (Sekme) Modeli
        public class SkillCategory
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string TabColor { get; set; }
            public List<SkillDefinition> Skills { get; set; } = new List<SkillDefinition>();
        }

        public class PluginConfig
        {
            public float BaseXpMultiplier = 1.0f;
            public string UiBackgroundColor = "0.05 0.05 0.05 0.98";
            public List<SkillCategory> Categories = new List<SkillCategory>();
        }

        public class PlayerData
        {
            public float TotalXp;
            public int Level = 1;
            public int SkillPoints;
            public Dictionary<string, int> Skills = new Dictionary<string, int>(); // Skill Id -> Level
        }

        #endregion

        private PluginConfig _config;
        private Dictionary<ulong, PlayerData> _playerData = new Dictionary<ulong, PlayerData>();
        private const string UiMainPanel = "SkillTree_Main";
        private const string UiHudPanel = "SkillTree_HUD"; // XP barı için yeni panel
        private const string UiGdoPanel = "SkillTree_GDO";
        private readonly Dictionary<ulong, double> _gdoCooldownUntil = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, int> _weaponBaseMagazine = new Dictionary<ulong, int>();

        #region Başlatma ve Kayıt İşlemleri

        private void Init()
        {
            LoadConfigVariables();
            _playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>(Name) ?? new Dictionary<ulong, PlayerData>();
            
            // Health Regen & Global Ticks
            timer.Every(10f, () => {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player.IsDead() || player.IsWounded()) continue;
                    
                    var data = GetPlayer(player.userID);
                    if (data.Skills.TryGetValue("health_regen", out int regenLvl) && regenLvl > 0)
                    {
                        float regenAmount = GetSkillBonus("health_regen", regenLvl) / 5f; // Örn 20 ise -> 4 can (10 saniyede bir)
                        if (player.health < player._maxHealth)
                        {
                            player.Heal(regenAmount);
                        }
                    }
                }
            });

            // Extended Mag gibi eldeki silaha bağlı bonusları düzenli uygula
            timer.Every(1f, () =>
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected || player.IsDead()) continue;
                    ApplyWeaponBonuses(player);
                }
            });
        }

        private void Unload()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _playerData);
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiHudPanel);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            DrawHUD(player);
        }

        // Sunucu Wipe yediğinde yetenekleri sıfırla
        private void OnNewSave(string filename)
        {
            _playerData.Clear();
            SaveConfig();
            PrintWarning("Sunucu wipe yedi, tüm yetenek verileri sıfırlandı!");
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig
            {
                Categories = new List<SkillCategory>
                {
                    new SkillCategory
                    {
                        Id = "woodcutting", DisplayName = "Woodcutting", TabColor = "0.2 0.8 0.2 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "woodcutting_yield", DisplayName = "Woodcutting Yield", Description = "Odun keserken elde edilen miktarı artırır.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/axe-in-log.svg", MaxLevel = 10, BonusPerLevel = 200.0f, RequiredCategoryLevel = 0 },
                            new SkillDefinition { Id = "woodcutting_coal", DisplayName = "Woodcutting Coal", Description = "Odun keserken kömür elde etme şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/coal-wagon.svg", MaxLevel = 5, BonusPerLevel = 5.0f, RequiredCategoryLevel = 0 },
                            new SkillDefinition { Id = "woodcutting_tool_durability", DisplayName = "Tool Durability", Description = "Odun kesme aletlerinin dayanıklılık kaybını azaltır.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/lorc/stone-axe.svg", MaxLevel = 5, BonusPerLevel = 10.0f, RequiredCategoryLevel = 0 },

                            new SkillDefinition { Id = "instant_chop", DisplayName = "Instant Chop", Description = "Bir ağacı tek vuruşta kesme şansı verir.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/magic-axe.svg", MaxLevel = 5, BonusPerLevel = 10.0f, RequiredCategoryLevel = 10 },
                            new SkillDefinition { Id = "woodcutting_luck", DisplayName = "Woodcutting Luck", Description = "Ağaç kesiminde ekstra eşya bulma şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/shamrock.svg", MaxLevel = 5, BonusPerLevel = 3.0f, RequiredCategoryLevel = 10 },
                            new SkillDefinition { Id = "woodcutting_hotspot", DisplayName = "Woodcutting Hotspot", Description = "Her vuruşta çarpı(X) noktasına vurmuş sayılma şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/hammer-break.svg", MaxLevel = 5, BonusPerLevel = 5.0f, RequiredCategoryLevel = 10 },
                            new SkillDefinition { Id = "regrowth", DisplayName = "Regrowth", Description = "Kestiğiniz ağacın anında yeniden büyüme şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/tree-growth.svg", MaxLevel = 5, BonusPerLevel = 2.0f, RequiredCategoryLevel = 15 },
                            
                            new SkillDefinition { Id = "woodcutting_ultimate", DisplayName = "Woodcutting Ultimate", Description = "Bir ağacı kestiğinizde etraftaki ağaçları da keser.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/lorc/hatchets.svg", MaxLevel = 1, BonusPerLevel = 100.0f, RequiredCategoryLevel = 25 }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "mining", DisplayName = "Mining", TabColor = "0.5 0.5 0.5 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "mining_yield", DisplayName = "Mining Yield", Description = "Maden kazarken elde edilen miktarı artırır.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/miner.svg", MaxLevel = 10, BonusPerLevel = 200.0f },
                            new SkillDefinition { Id = "instant_mine", DisplayName = "Instant Mine", Description = "Madeni tek vuruşta kırma şansı verir.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/dynamite.svg", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "smelt_on_mine", DisplayName = "Smelt On Mine", Description = "Maden kazarken cevherin otomatik erimiş gelme şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/furnace.svg", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "mining_luck", DisplayName = "Mining Luck", Description = "Maden kazarken ekstra özel eşya bulma şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/shamrock.svg", MaxLevel = 5, BonusPerLevel = 1.0f },
                            new SkillDefinition { Id = "mining_tool_durability", DisplayName = "Tool Durability", Description = "Maden aletlerinin kırılmasını yavaşlatır.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/war-pick.svg", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "mining_hotspot", DisplayName = "Mining Hotspot", Description = "Her vuruşta madenin parlayan noktasına vurma şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/delapouite/hammer-break.svg", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "node_spawn_chance", DisplayName = "Node Spawn Chance", Description = "Madeni bitirdiğinizde yenisinin anında çıkma şansı.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/faithtoken/ore.svg", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "mining_ultimate", DisplayName = "Mining Ultimate", Description = "Etraftaki tüm madenleri tarayan komutu açar.", IconUrl = "https://game-icons.net/icons/ffffff/000000/1x1/lorc/gems.svg", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "skinning", DisplayName = "Skinning", TabColor = "0.7 0.4 0.2 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "skinning_yield", DisplayName = "Skinning Yield", Description = "Hayvan yüzerken elde edilen miktarı artırır.", IconUrl = "https://i.imgur.com/example_knife.png", MaxLevel = 10, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "instant_skin", DisplayName = "Instant Skin", Description = "Hayvanı tek vuruşta tamamen yüzme şansı.", IconUrl = "https://i.imgur.com/example_fast.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "skinning_tool_durability", DisplayName = "Tool Durability", Description = "Yüzme aletlerinin dayanıklılığını korur.", IconUrl = "https://i.imgur.com/example_durability.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "skinning_cook", DisplayName = "Skinning Cook", Description = "Topladığınız etlerin fırından çıkmış gibi pişmiş olma şansı.", IconUrl = "https://i.imgur.com/example_cook.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "skinning_luck", DisplayName = "Skinning Luck", Description = "Hayvan yüzerken nadir eşyalar bulma şansı.", IconUrl = "https://i.imgur.com/example_luck.png", MaxLevel = 5, BonusPerLevel = 2.0f },
                            new SkillDefinition { Id = "animal_tracker", DisplayName = "Animal Tracker", Description = "Etraftaki hayvanların yerini bulma komutu açılır (/track).", IconUrl = "https://i.imgur.com/example_track.png", MaxLevel = 1, BonusPerLevel = 100.0f },
                            new SkillDefinition { Id = "skinning_ultimate", DisplayName = "Skinning Ultimate", Description = "Hayvan türüne göre öldürdükten sonra özel güç kazanma.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "farming", DisplayName = "Farming", TabColor = "0.2 0.8 0.2 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "harvest_wild_yield", DisplayName = "Harvest Wild", Description = "Doğadan toplanan kenevir, böğürtlen gibi ürünlerin miktarını artırır.", IconUrl = "https://img.itch.zone/aW1hZ2UvMzczOTgwNS8yMjI1NzQzNi5wbmc=/347x500/djgrVI.png", MaxLevel = 5, BonusPerLevel = 15.0f },
                            new SkillDefinition { Id = "harvest_grown_yield", DisplayName = "Harvest Grown", Description = "Tarladan toplanan ekinlerin miktarını artırır.", IconUrl = "https://img.itch.zone/aW1hZ2UvMzczOTgwNS8yMjI1NzQzNi5wbmc=/347x500/djgrVI.png", MaxLevel = 5, BonusPerLevel = 15.0f },
                            new SkillDefinition { Id = "harvesting_luck", DisplayName = "Harvesting Luck", Description = "Toplamalar sırasında sürpriz eşyalar düşürme şansı.", IconUrl = "https://i.imgur.com/example_luck.png", MaxLevel = 5, BonusPerLevel = 2.0f },
                            new SkillDefinition { Id = "harvester_ultimate", DisplayName = "Harvester Ultimate", Description = "Ekinlerin genetiğini yönetmenizi (G, Y, vs.) sağlar.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "combat", DisplayName = "Combat", TabColor = "0.8 0.2 0.2 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "pvp_damage", DisplayName = "PVP Damage", Description = "Diğer oyunculara verilen hasarı artırır.", IconUrl = "https://i.imgur.com/example_sword.png", MaxLevel = 5, BonusPerLevel = 0.02f },
                            new SkillDefinition { Id = "pvp_shield", DisplayName = "PVP Shield", Description = "Diğer oyunculardan alınan hasarı azaltır.", IconUrl = "https://i.imgur.com/example_shield.png", MaxLevel = 5, BonusPerLevel = 0.02f },
                            new SkillDefinition { Id = "pvp_critical", DisplayName = "PVP Critical", Description = "PVP savaşlarında kritik (çift) hasar vurma şansı.", IconUrl = "https://i.imgur.com/example_crit.png", MaxLevel = 5, BonusPerLevel = 1.0f },
                            new SkillDefinition { Id = "human_npc_damage", DisplayName = "NPC Damage", Description = "Bilim adamlarına (Scientist) verilen hasarı artırır.", IconUrl = "https://i.imgur.com/example_npc.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "free_bullet_chance", DisplayName = "Free Bullet", Description = "Ateş ederken mermi harcamama şansı.", IconUrl = "https://i.imgur.com/example_bullet.png", MaxLevel = 5, BonusPerLevel = 2.0f },
                            new SkillDefinition { Id = "extended_mag", DisplayName = "Extended Mag", Description = "Silahlarınızın mermi kapasitesini artırır.", IconUrl = "https://i.imgur.com/example_mag.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "combat_ultimate", DisplayName = "Combat Ultimate", Description = "Vurduğunuz hasarın belli bir miktarını size can olarak geri verir (Lifesteal).", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "medical", DisplayName = "Survival / Medical", TabColor = "0.2 0.6 0.8 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "health_regen", DisplayName = "Health Regen", Description = "Pasif olarak belirli aralıklarla can kazanımı sağlar.", IconUrl = "https://i.imgur.com/example_heart.png", MaxLevel = 5, BonusPerLevel = 20.0f },
                            new SkillDefinition { Id = "double_bandage_heal", DisplayName = "Double Bandage", Description = "Bandaj/şırınga kullanımında seviye başına +5 ekstra can verir (max +15).", IconUrl = "https://i.imgur.com/example_bandage.png", MaxLevel = 3, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "radiation_reduction", DisplayName = "Radiation Resist", Description = "Radyasyondan alınan hasarı büyük oranda düşürür.", IconUrl = "https://i.imgur.com/example_rad.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "extra_food", DisplayName = "Extra Food", Description = "Yiyecek ve içeceklerin kalorisini ve suyunu artırır.", IconUrl = "https://i.imgur.com/example_food.png", MaxLevel = 5, BonusPerLevel = 15.0f },
                            new SkillDefinition { Id = "fire_damage_reduction", DisplayName = "Fire Resist", Description = "Ateşten alınan hasarı azaltır.", IconUrl = "https://i.imgur.com/example_fire.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "fall_damage_reduction", DisplayName = "Fall Resist", Description = "Yüksekten düşme hasarını azaltır.", IconUrl = "https://i.imgur.com/example_fall.png", MaxLevel = 5, BonusPerLevel = 15.0f },
                            new SkillDefinition { Id = "medical_ultimate", DisplayName = "Medical Ultimate", Description = "Öldüğünüzde eşyalarınızla olduğunuz yerde tekrar dirilme şansı verir.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "scavenging", DisplayName = "Scavenging", TabColor = "0.8 0.8 0.2 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "extra_scrap_barrel", DisplayName = "Extra Scrap Barrel", Description = "Varilleri kırarken daha fazla hurda elde etme şansı.", IconUrl = "https://i.imgur.com/example_scrap.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "barrel_smasher", DisplayName = "Barrel Smasher", Description = "Elimizdeki her eşya ile varilleri tek vuruşta kırabilme.", IconUrl = "https://i.imgur.com/example_smash.png", MaxLevel = 1, BonusPerLevel = 100.0f },
                            new SkillDefinition { Id = "component_chest", DisplayName = "Component Chest", Description = "Kutulardan çok daha fazla bileşen çıkarma şansı.", IconUrl = "https://i.imgur.com/example_chest.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "loot_pickup", DisplayName = "Loot Pickup", Description = "Varilleri kırdığınızda eşyalar yere düşmeden direkt envanterinize gelir.", IconUrl = "https://i.imgur.com/example_magnet.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "scavengers_ultimate", DisplayName = "Scavengers Ultimate", Description = "Kırılan varillerdeki eşyalar direkt scrap ve kaynaklarına parçalanır.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "crafting", DisplayName = "Building & Crafting", TabColor = "0.6 0.6 0.6 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "craft_speed", DisplayName = "Craft Speed", Description = "El yapımı eşya üretim hızınızı artırır.", IconUrl = "https://i.imgur.com/example_craft.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "craft_refund", DisplayName = "Craft Refund", Description = "Ürettiğiniz ürünlerin malzemelerinin bir kısmını geri kazanma şansı.", IconUrl = "https://i.imgur.com/example_refund.png", MaxLevel = 5, BonusPerLevel = 3.0f },
                            new SkillDefinition { Id = "upgrade_refund", DisplayName = "Upgrade Refund", Description = "Duvarları ve zeminleri metal/taş yaparken kaynağın geri dönme şansı.", IconUrl = "https://i.imgur.com/example_hammer.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "recycler_efficiency", DisplayName = "Recycler Efficiency", Description = "Geri dönüşüm makinesinden daha çok ve hızlı kaynak çıkar.", IconUrl = "https://i.imgur.com/example_recycler.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "build_craft_ultimate", DisplayName = "Builder Ultimate", Description = "Puzzle kapılarında yanlış renk kartlar bile kapıyı açar.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    },
                    new SkillCategory
                    {
                        Id = "vehicles", DisplayName = "Vehicles", TabColor = "0.7 0.3 0.6 0.8",
                        Skills = new List<SkillDefinition>
                        {
                            new SkillDefinition { Id = "boat_speed", DisplayName = "Boat Speed", Description = "Teknelerde turbo basarken hızı muazzam artırır.", IconUrl = "https://i.imgur.com/example_boat.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "heli_fuel_rate", DisplayName = "Heli Fuel Rate", Description = "Helikopter sürerken yakıt harcama oranınız gözle görülür şekilde azalır.", IconUrl = "https://i.imgur.com/example_heli.png", MaxLevel = 5, BonusPerLevel = 10.0f },
                            new SkillDefinition { Id = "vehicle_mechanic", DisplayName = "Vehicle Mechanic", Description = "Araçlarınızı tamir ederken gereken malzeme miktarını düşürür / sıfırlar.", IconUrl = "https://i.imgur.com/example_repair.png", MaxLevel = 5, BonusPerLevel = 15.0f },
                            new SkillDefinition { Id = "riding_speed", DisplayName = "Riding Speed", Description = "Atların sprint atarkenki koşu mesafesini ve hızını artırır.", IconUrl = "https://i.imgur.com/example_horse.png", MaxLevel = 5, BonusPerLevel = 5.0f },
                            new SkillDefinition { Id = "vehicle_ultimate", DisplayName = "Vehicle Ultimate", Description = "İçinde / üstünde sürdüğünüz aracın hasar almasını engeller.", IconUrl = "https://i.imgur.com/example_ultimate.png", MaxLevel = 1, BonusPerLevel = 100.0f }
                        }
                    }
                }
            };
            SaveConfig();
        }

        private void LoadConfigVariables()
        {
            _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private PlayerData GetPlayer(ulong userId)
        {
            if (!_playerData.TryGetValue(userId, out var data))
            {
                data = new PlayerData();
                _playerData[userId] = data;
            }
            return data;
        }

        #endregion

        #region Oyun İçi Mekanikler (Hooks)
        
        // Helper: Config'den dinamik bonus çeker, tam sayıysa (örn 15.0) yüzdeye (0.15) çevirir
        private float GetSkillBonus(string skillId, int level)
        {
            if (level <= 0) return 0f;
            foreach (var cat in _config.Categories)
            {
                var skill = cat.Skills.FirstOrDefault(s => s.Id == skillId);
                if (skill != null) 
                {
                    float bonus = skill.BonusPerLevel * level;
                    // Eğer config'de 15.0 gibi tamsayı girdiyse %15 (0.15) olarak kullanmak için çevir
                    if (skill.BonusPerLevel >= 1f) return bonus / 100f; 
                    return bonus;
                }
            }
            return 0f;
        }

        private void ApplyWeaponBonuses(BasePlayer player)
        {
            var data = GetPlayer(player.userID);
            Item active = player.GetActiveItem();
            if (active == null) return;

            var projectile = active.GetHeldEntity() as BaseProjectile;
            if (projectile?.primaryMagazine == null) return;

            ulong uid = active.uid.Value;
            if (!_weaponBaseMagazine.TryGetValue(uid, out int baseCap))
            {
                baseCap = projectile.primaryMagazine.capacity;
                _weaponBaseMagazine[uid] = baseCap;
            }

            if (data.Skills.TryGetValue("extended_mag", out int extLvl) && extLvl > 0)
            {
                int targetCap = baseCap + (extLvl * 2); // Her seviye +2 mermi
                if (projectile.primaryMagazine.capacity != targetCap)
                {
                    projectile.primaryMagazine.capacity = targetCap;
                    if (projectile.primaryMagazine.contents > targetCap)
                        projectile.primaryMagazine.contents = targetCap;
                    projectile.SendNetworkUpdateImmediate();
                }
            }
            else if (projectile.primaryMagazine.capacity != baseCap)
            {
                projectile.primaryMagazine.capacity = baseCap;
                if (projectile.primaryMagazine.contents > baseCap)
                    projectile.primaryMagazine.contents = baseCap;
                projectile.SendNetworkUpdateImmediate();
            }
        }

        private void GiveRandomLuckItem(BasePlayer player)
        {
            var pool = new[]
            {
                ("apple", 1),
                ("mushroom", 2),
                ("cloth", 20),
                ("scrap", 5),
                ("metal.fragments", 25),
                ("sulfur", 30),
                ("lowgradefuel", 20)
            };

            var pick = pool[UnityEngine.Random.Range(0, pool.Length)];
            Item reward = ItemManager.CreateByName(pick.Item1, pick.Item2);
            if (reward != null)
            {
                player.GiveItem(reward);
                player.ChatMessage($"<color=#00e5ff>Şanslısın! Bonus eşya buldun: {reward.info.displayName.english} x{pick.Item2}</color>");
            }
        }

        // Ağaçtaki çarpıya (X) veya madendeki parlayan noktaya (node) tam isabetle vurulduğunda
        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null) return;
            var data = GetPlayer(player.userID);
            
            // Tam isabet edildiğinde ekstra 25 XP kazandırır
            AddXp(player, data, 25f * _config.BaseXpMultiplier);
        }

        // Odun ve Maden toplama mekaniği
        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity.ToPlayer();
            if (player == null || item == null) return;

            var data = GetPlayer(player.userID);
            bool isWood = item.info.shortname.Contains("wood");
            bool isOre = item.info.shortname.Contains("stones") || item.info.shortname.Contains("metal") || item.info.shortname.Contains("sulfur") || item.info.shortname.Contains("hq.metal");
            bool isSkin = item.info.shortname.Contains("meat") || item.info.shortname.Contains("bone") || item.info.shortname.Contains("leather") || item.info.shortname.Contains("fat") || item.info.shortname.Contains("cloth");

            // --- XP KAZANIMI ---
            if (isWood) AddXp(player, data, item.amount * 0.2f * _config.BaseXpMultiplier); 
            else if (isOre) AddXp(player, data, item.amount * 0.3f * _config.BaseXpMultiplier); 
            else if (isSkin) AddXp(player, data, item.amount * 0.5f * _config.BaseXpMultiplier);

            // --- SKINNING YETENEKLERİ ---
            if (isSkin)
            {
                if (data.Skills.TryGetValue("skinning_yield", out int skinYieldLvl) && skinYieldLvl > 0)
                {
                    float bonus = GetSkillBonus("skinning_yield", skinYieldLvl);
                    int extra = Mathf.RoundToInt(item.amount * bonus);
                    if (extra > 0) item.amount += extra;
                }

                if (data.Skills.TryGetValue("skinning_cook", out int cookLvl) && cookLvl > 0)
                {
                    float chance = GetSkillBonus("skinning_cook", cookLvl);
                    if (item.info.shortname.EndsWith(".raw") && UnityEngine.Random.Range(0f, 1f) <= chance)
                    {
                        string cookedName = item.info.shortname.Replace(".raw", ".cooked");
                        ItemDefinition def = ItemManager.FindItemDefinition(cookedName);
                        if (def != null) item.info = def;
                    }
                }
            }
            
            // --- WOODCUTTING YETENEKLERİ ---
            if (isWood)
            {
                // Woodcutting Yield: Toplanan miktarı artırır
                if (data.Skills.TryGetValue("woodcutting_yield", out int woodYieldLvl) && woodYieldLvl > 0)
                {
                    float bonus = GetSkillBonus("woodcutting_yield", woodYieldLvl);
                    int extra = Mathf.RoundToInt(item.amount * bonus);
                    if (extra > 0) item.amount += extra;
                }

                // Instant Chop: Ağacı tek vuruşta kesme
                if (data.Skills.TryGetValue("instant_chop", out int chopLvl) && chopLvl > 0)
                {
                    float chance = GetSkillBonus("instant_chop", chopLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance)
                    {
                        var tree = dispenser.GetComponent<TreeEntity>();
                        if (tree != null && tree.health > 0)
                        {
                            GiveRemainingDispenserItems(dispenser, player, data, "woodcutting_yield");
                            tree.Kill(BaseNetworkable.DestroyMode.None);
                        }
                    }
                }

                // Woodcutting Luck: Ekstra eşya bulma
                if (data.Skills.TryGetValue("woodcutting_luck", out int luckLvl) && luckLvl > 0)
                {
                    float chance = GetSkillBonus("woodcutting_luck", luckLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance) 
                    {
                        GiveRandomLuckItem(player);
                    }
                }
            }
            
            // --- MINING YETENEKLERİ ---
            if (isOre)
            {
                // Mining Yield: Maden miktarını artırır
                if (data.Skills.TryGetValue("mining_yield", out int minYieldLvl) && minYieldLvl > 0)
                {
                    float bonus = GetSkillBonus("mining_yield", minYieldLvl);
                    int extra = Mathf.RoundToInt(item.amount * bonus);
                    if (extra > 0) item.amount += extra;
                }

                // Instant Mine: Madeni tek vuruşta kırma
                if (data.Skills.TryGetValue("instant_mine", out int instMineLvl) && instMineLvl > 0)
                {
                    float chance = GetSkillBonus("instant_mine", instMineLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance) 
                    {
                        var ore = dispenser.GetComponent<OreResourceEntity>();
                        if (ore != null && ore.health > 0)
                        {
                            GiveRemainingDispenserItems(dispenser, player, data, "mining_yield");
                            ore.Kill(BaseNetworkable.DestroyMode.None);
                        }
                    }
                }

                // Smelt On Mine: Cevherleri fırındaymış gibi otomatik eritir
                if (data.Skills.TryGetValue("smelt_on_mine", out int smeltLvl) && smeltLvl > 0)
                {
                    float chance = GetSkillBonus("smelt_on_mine", smeltLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance) 
                    {
                        string smeltedName = "";
                        if (item.info.shortname == "metal.ore") smeltedName = "metal.fragments";
                        else if (item.info.shortname == "sulfur.ore") smeltedName = "sulfur";
                        else if (item.info.shortname == "hq.metal.ore") smeltedName = "metal.refined";

                        if (!string.IsNullOrEmpty(smeltedName))
                        {
                            ItemDefinition def = ItemManager.FindItemDefinition(smeltedName);
                            if (def != null)
                            {
                                item.info = def; // Eşyayı dönüşmüş haliyle değiştir
                            }
                        }
                    }
                }
            }
        }

        private void GiveRemainingDispenserItems(ResourceDispenser dispenser, BasePlayer player, PlayerData data, string yieldSkillId)
        {
            if (dispenser.containedItems == null) return;
            
            float bonus = 0f;
            if (data.Skills.TryGetValue(yieldSkillId, out int yLvl) && yLvl > 0)
            {
                bonus = GetSkillBonus(yieldSkillId, yLvl);
            }

            foreach (var itemAmount in dispenser.containedItems)
            {
                if (itemAmount.amount > 0)
                {
                    int amount = Mathf.RoundToInt(itemAmount.amount);
                    int extra = Mathf.RoundToInt(amount * bonus);
                    amount += extra;
                    
                    string shortname = itemAmount.itemDef.shortname;

                    // Eğer maden kazılıyorsa ve smelt on mine yeteneği varsa otomatik erit
                    if (yieldSkillId == "mining_yield" && data.Skills.TryGetValue("smelt_on_mine", out int smeltLvl) && smeltLvl > 0)
                    {
                        float chance = GetSkillBonus("smelt_on_mine", smeltLvl);
                        if (UnityEngine.Random.Range(0f, 1f) <= chance) 
                        {
                            if (shortname == "metal.ore") shortname = "metal.fragments";
                            else if (shortname == "sulfur.ore") shortname = "sulfur";
                            else if (shortname == "hq.metal.ore") shortname = "metal.refined";
                        }
                    }

                    Item toGive = ItemManager.CreateByName(shortname, amount);
                    if (toGive != null)
                    {
                        player.GiveItem(toGive);
                        player.Command("note.inv", toGive.info.itemid, amount);
                    }
                    itemAmount.amount = 0; // Kırıldığında etrafa fazladan saçılmaması veya bug olmaması için sıfırlıyoruz.
                }
            }
        }

        // Savaş (Combat) Mekaniği: Başka bir oyuncuya vurulduğunda ekstra hasar ve kalkan
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || entity == null) return;

            BasePlayer attacker = info.InitiatorPlayer;
            BasePlayer victim = entity as BasePlayer;

            // Varil Kırıcı
            if (attacker != null && entity.ShortPrefabName.Contains("barrel"))
            {
                var attackerData = GetPlayer(attacker.userID);
                if (attackerData.Skills.TryGetValue("barrel_smasher", out int smashLvl) && smashLvl > 0)
                {
                    info.damageTypes.ScaleAll(100f); // Tek atmasını sağla
                }
            }

            if (victim != null)
            {
                var victimData = GetPlayer(victim.userID);

                // Düşme, Radyasyon, Ateş azaltmaları (Medical)
                if (victimData.Skills.TryGetValue("radiation_reduction", out int radLvl) && radLvl > 0)
                {
                    info.damageTypes.Scale(DamageType.Radiation, Mathf.Max(0.1f, 1f - GetSkillBonus("radiation_reduction", radLvl)));
                }
                if (victimData.Skills.TryGetValue("fire_damage_reduction", out int fireLvl) && fireLvl > 0)
                {
                    info.damageTypes.Scale(DamageType.Heat, Mathf.Max(0.1f, 1f - GetSkillBonus("fire_damage_reduction", fireLvl)));
                }
                if (victimData.Skills.TryGetValue("fall_damage_reduction", out int fallLvl) && fallLvl > 0)
                {
                    info.damageTypes.Scale(DamageType.Fall, Mathf.Max(0.1f, 1f - GetSkillBonus("fall_damage_reduction", fallLvl)));
                }
            }

            if (attacker != null && victim != null && attacker != victim)
            {
                // PVP Damage (Hasar Artırma)
                var attackerData = GetPlayer(attacker.userID);
                if (attackerData.Skills.TryGetValue("pvp_damage", out int dmgLvl) && dmgLvl > 0)
                {
                    float bonus = GetSkillBonus("pvp_damage", dmgLvl);
                    float extraDamageMultiplier = 1f + bonus; 
                    info.damageTypes.ScaleAll(extraDamageMultiplier);
                }

                // PVP Shield (Hasar Düşürme)
                var victimData = GetPlayer(victim.userID);
                if (victimData.Skills.TryGetValue("pvp_shield", out int shieldLvl) && shieldLvl > 0)
                {
                    float bonus = GetSkillBonus("pvp_shield", shieldLvl);
                    float reductionMultiplier = 1f - bonus;
                    if (reductionMultiplier < 0.1f) reductionMultiplier = 0.1f; // Hasar %90'dan fazla düşmesin
                    info.damageTypes.ScaleAll(reductionMultiplier);
                }
            }
        }

        // Farming (Doğal veya ekilmiş) toplama mekanikleri
        // Bandaj / İyileşme (Medical)
        private void OnHealingItemUse(MedicalTool item, BasePlayer target)
        {
            if (target == null || item == null) return;
            var data = GetPlayer(target.userID);

            if (item.ShortPrefabName.Contains("bandage") || item.ShortPrefabName.Contains("syringe"))
            {
                if (data.Skills.TryGetValue("double_bandage_heal", out int lvl) && lvl > 0)
                {
                    int clamped = Mathf.Clamp(lvl, 1, 3);
                    float extraHeal = clamped * 5f; // 1=+5, 2=+10, 3=+15

                    timer.Once(0.2f, () => {
                        if (target != null && target.IsConnected && !target.IsDead())
                        {
                            target.Heal(extraHeal);
                            target.ChatMessage($"<color=#00e5ff>Bandaj Ustalığı: +{extraHeal:0} ekstra can!</color>");
                        }
                    });
                }
            }
        }

        // Tüketilebilir (Yemek)
        private void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (player == null || action != "consume") return;
            var data = GetPlayer(player.userID);
            
            if (data.Skills.TryGetValue("extra_food", out int foodLvl) && foodLvl > 0)
            {
                float bonus = GetSkillBonus("extra_food", foodLvl);
                if (item.info.category == ItemCategory.Food)
                {
                    timer.Once(0.2f, () => {
                        if (player != null && player.IsConnected)
                        {
                            player.metabolism.calories.Add(bonus);
                            player.metabolism.hydration.Add(bonus);
                        }
                    });
                }
            }
        }

        // Scavenging: Variller ve Kutular
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var player = info.InitiatorPlayer;
            if (player == null) return;

            var data = GetPlayer(player.userID);
            bool isBarrel = entity.ShortPrefabName.Contains("barrel");
            bool isTree = entity is TreeEntity;

            if (isTree)
            {
                // Regrowth: Kesilen ağacın aynı yere tekrar doğması
                if (data.Skills.TryGetValue("regrowth", out int regrowthLvl) && regrowthLvl > 0)
                {
                    float chance = GetSkillBonus("regrowth", regrowthLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance)
                    {
                        string prefabName = entity.PrefabName;
                        Vector3 pos = entity.transform.position;
                        Quaternion rot = entity.transform.rotation;

                        timer.Once(0.25f, () =>
                        {
                            var newTree = GameManager.server.CreateEntity(prefabName, pos, rot, true);
                            if (newTree != null) newTree.Spawn();
                        });
                    }
                }

                // Woodcutting Ultimate: 50m içindeki en yakın 5 ağacı da kes
                if (data.Skills.TryGetValue("woodcutting_ultimate", out int ultLvl) && ultLvl > 0)
                {
                    var nearbyTrees = BaseNetworkable.serverEntities
                        .OfType<TreeEntity>()
                        .Where(t => t != null && !t.IsDestroyed && Vector3.Distance(t.transform.position, entity.transform.position) <= 50f)
                        .OrderBy(t => Vector3.Distance(t.transform.position, entity.transform.position))
                        .Take(5)
                        .ToList();

                    foreach (var tree in nearbyTrees)
                    {
                        if (tree == null || tree.IsDestroyed || tree == entity) continue;

                        var dispenser = tree.GetComponent<ResourceDispenser>();
                        if (dispenser != null)
                        {
                            GiveRemainingDispenserItems(dispenser, player, data, "woodcutting_yield");
                        }
                        tree.Kill(BaseNetworkable.DestroyMode.None);
                    }
                }
            }

            if (isBarrel)
            {
                AddXp(player, data, 10f * _config.BaseXpMultiplier);

                if (data.Skills.TryGetValue("extra_scrap_barrel", out int scrapLvl) && scrapLvl > 0)
                {
                    float chance = GetSkillBonus("extra_scrap_barrel", scrapLvl);
                    if (UnityEngine.Random.Range(0f, 1f) <= chance)
                    {
                        Item scrap = ItemManager.CreateByName("scrap", UnityEngine.Random.Range(2, 5));
                        if (scrap == null) return;
                        
                        if (data.Skills.TryGetValue("loot_pickup", out int pickupLvl) && pickupLvl > 0 && UnityEngine.Random.Range(0f, 1f) <= GetSkillBonus("loot_pickup", pickupLvl))
                        {
                            player.GiveItem(scrap);
                        }
                        else 
                        {
                            scrap.Drop(entity.transform.position, Vector3.up);
                        }
                    }
                }
            }
        }

        private void OnCollectiblePickup(Item item, BasePlayer player)
        {
            if (player == null || item == null) return;
            var data = GetPlayer(player.userID);
            
            bool isWood = item.info.shortname.Contains("wood");
            bool isOre = item.info.shortname.Contains("stones") || item.info.shortname.Contains("metal") || item.info.shortname.Contains("sulfur") || item.info.shortname.Contains("hq.metal");

            if (isWood)
            {
                AddXp(player, data, 15f * _config.BaseXpMultiplier); // Yerdeki odun stach lerinden 15 XP
                if (data.Skills.TryGetValue("woodcutting_yield", out int lvl) && lvl > 0)
                {
                    item.amount += Mathf.RoundToInt(item.amount * GetSkillBonus("woodcutting_yield", lvl));
                }
            }
            else if (isOre)
            {
                AddXp(player, data, 20f * _config.BaseXpMultiplier); // Yerdeki maden stach lerinden 20 XP
                if (data.Skills.TryGetValue("mining_yield", out int lvl) && lvl > 0)
                {
                    item.amount += Mathf.RoundToInt(item.amount * GetSkillBonus("mining_yield", lvl));
                }
            }
            else 
            {
                AddXp(player, data, 25f * _config.BaseXpMultiplier); // Kenevir, Mantar, Böğürtlen, Patates vb. için 25 XP
                if (data.Skills.TryGetValue("harvest_wild_yield", out int level) && level > 0)
                {
                    float bonus = GetSkillBonus("harvest_wild_yield", level);
                    int extra = Mathf.RoundToInt(item.amount * bonus);
                    if (extra > 0) item.amount += extra;
                }
            }
        }

        private void OnGrowableGather(GrowableEntity plant, Item item, BasePlayer player)
        {
            if (player == null || item == null) return;
            var data = GetPlayer(player.userID);
            AddXp(player, data, 5f * _config.BaseXpMultiplier);

            if (data.Skills.TryGetValue("harvest_grown_yield", out int level) && level > 0)
            {
                float chance = GetSkillBonus("harvest_grown_yield", level);
                if (UnityEngine.Random.Range(0f, 1f) <= chance)
                {
                    item.amount += Mathf.RoundToInt(item.amount * 1f); 
                }
            }
        }

        private float GetXpRequirement(int level)
        {
            if (level == 1) return 50f;
            if (level == 2) return 100f;
            if (level == 3) return 150f;
            if (level == 4) return 300f;
            if (level == 5) return 500f;
            
            // 5. seviyeden sonra sabit bir şekilde artmaya devam eder
            return 500f + ((level - 5) * 250f);
        }

        private void AddXp(BasePlayer player, PlayerData data, float amount)
        {
            data.TotalXp += amount;
            
            float xpNeeded = GetXpRequirement(data.Level);
            if (data.TotalXp >= xpNeeded)
            {
                data.TotalXp -= xpNeeded;
                data.Level++;
                data.SkillPoints++;
                
                // Seviye atladığında hoş bir ses çıkart (Rust içerisindeki hediye paketi açılış sesi ve efekti)
                Effect.server.Run("assets/prefabs/misc/xmas/presents/effects/unwrap.prefab", player.transform.position);
                
                SendReply(player, $"<color=#00e5ff>SEVİYE ATLADIN!</color> Yeni Seviye: <color=#ffe500>{data.Level}</color> - 1 Yetenek Puanı kazandın, <color=#ffe500>/mst</color> komutu ile yeteneklerini yönetebilirsin.");
            }
            DrawHUD(player); // HUD'u güncelle
        }

        #endregion

        #region Modern Arayüz (CUI) Oluşturucu

        // Komut: /skill, /mst veya /skill <kategori_id>
        [ChatCommand("skill")]
        private void CmdOpenUI(BasePlayer player, string command, string[] args)
        {
            string activeTab = args.Length > 0 ? args[0] : _config.Categories.FirstOrDefault()?.Id;
            DrawUI(player, activeTab);
        }

        [ChatCommand("mst")]
        private void CmdOpenUIMST(BasePlayer player, string command, string[] args)
        {
            if (args.Length >= 3 && args[0].ToLower() == "add")
            {
                if (!player.IsAdmin)
                {
                    SendReply(player, "Bunun için yetkiniz yok.");
                    return;
                }
                
                string targetName = args[1];
                if (!float.TryParse(args[2], out float xpAmount)) return;

                BasePlayer target = BasePlayer.Find(targetName);
                if (target == null)
                {
                    SendReply(player, $"Oyuncu bulunamadı: {targetName}");
                    return;
                }

                var data = GetPlayer(target.userID);
                AddXp(target, data, xpAmount);
                SendReply(player, $"{target.displayName} adlı oyuncuya {xpAmount} XP verildi.");
                return;
            }

            CmdOpenUI(player, command, args);
        }

        [ChatCommand("gdo")]
        private void CmdGdo(BasePlayer player, string command, string[] args)
        {
            var data = GetPlayer(player.userID);
            if (!data.Skills.TryGetValue("harvester_ultimate", out int lvl) || lvl <= 0)
            {
                SendReply(player, "Bu panel için Harvester Ultimate açmalısınız.");
                return;
            }

            if (args.Length >= 2 && args[0].Equals("claim", StringComparison.OrdinalIgnoreCase))
            {
                string plant = args[1].ToLower();
                string genes = args.Length >= 3 ? args[2].ToUpper() : "GGGGYY";
                TryClaimGdoSeed(player, plant, genes);
                return;
            }

            DrawGdoPanel(player);
        }

        [ConsoleCommand("mst.wipe")]
        private void ConsoleCmdWipe(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            
            _playerData.Clear();
            SaveData();
            
            foreach (var p in BasePlayer.activePlayerList)
            {
                DrawHUD(p);
                p.ChatMessage("<color=#ff0000>Sistem yetenek verileri sıfırlandı!</color>");
            }
            
            PrintWarning("Tüm oyuncuların yetenek verileri başarıyla sıfırlandı!");
            if (arg.Player() != null) SendReply(arg.Player(), "Tüm oyuncu verileri başarıyla sıfırlandı.");
        }

        [ChatCommand("mstwipe")]
        private void ChatCmdWipe(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, "Bunun için yetkiniz yok.");
                return;
            }
            
            _playerData.Clear();
            SaveData();
            
            foreach (var p in BasePlayer.activePlayerList)
            {
                DrawHUD(p);
                p.ChatMessage("<color=#ff0000>Sistem yetenek verileri sıfırlandı!</color>");
            }
            SendReply(player, "Tüm oyuncu verileri başarıyla sıfırlandı.");
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _playerData);
        }

        [ConsoleCommand("skilltree.action")]
        private void ConsoleCmdAction(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;

            string action = arg.Args[0]; // "tab", "upgrade" veya "close"

            if (action == "close")
            {
                CuiHelper.DestroyUi(player, UiMainPanel);
                return;
            }

            if (!arg.HasArgs(2)) return;
            string target = arg.Args[1];

            if (action == "tab")
            {
                DrawUI(player, target);
            }
            else if (action == "upgrade")
            {
                UpgradeSkill(player, target);
            }
        }

        [ConsoleCommand("gdo.close")]
        private void ConsoleCmdGdoClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiGdoPanel);
        }

        [ConsoleCommand("gdo.claim")]
        private void ConsoleCmdGdoClaim(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(2)) return;
            string plant = arg.Args[0].ToLower();
            string genes = arg.Args[1].ToUpper();
            TryClaimGdoSeed(player, plant, genes);
            DrawGdoPanel(player);
        }

        private bool IsValidGenes(string genes)
        {
            if (string.IsNullOrEmpty(genes) || genes.Length != 6) return false;
            foreach (char c in genes)
            {
                if (c != 'G' && c != 'Y' && c != 'H' && c != 'W' && c != 'X') return false;
            }
            return true;
        }

        private void TryClaimGdoSeed(BasePlayer player, string plant, string genes)
        {
            var data = GetPlayer(player.userID);
            if (!data.Skills.TryGetValue("harvester_ultimate", out int lvl) || lvl <= 0)
            {
                SendReply(player, "Bu panel için Harvester Ultimate açmalısınız.");
                return;
            }

            if (!IsValidGenes(genes))
            {
                SendReply(player, "Gen dizilimi 6 karakter olmalı ve sadece G,Y,H,W,X içermeli. Örn: GGGGYY");
                return;
            }

            double now = Interface.Oxide.Now;
            if (_gdoCooldownUntil.TryGetValue(player.userID, out double until) && until > now)
            {
                double left = until - now;
                SendReply(player, $"Yeni tohum için bekleme: {Mathf.CeilToInt((float)left)} sn");
                return;
            }

            string itemShortname;
            switch (plant)
            {
                case "hemp": itemShortname = "hemp.clone"; break;
                case "corn": itemShortname = "corn.clone"; break;
                case "pumpkin": itemShortname = "pumpkin.clone"; break;
                default:
                    SendReply(player, "Geçersiz bitki. Kullanım: /gdo claim hemp GGGGYY");
                    return;
            }

            Item clone = ItemManager.CreateByName(itemShortname, 1);
            if (clone == null)
            {
                SendReply(player, "Tohum oluşturulamadı.");
                return;
            }

            // Not: Bazı Rust sürümlerinde clone gen datası farklı formatta tutulur.
            // En azından item adıyla gen dizisini net şekilde veririz.
            clone.name = $"{plant.ToUpper()} Clone [{genes}]";
            player.GiveItem(clone);

            _gdoCooldownUntil[player.userID] = now + (15 * 60);
            SendReply(player, $"{plant} clone verildi. Gen: {genes}. Sonraki hak 15 dakika sonra.");
        }

        private void DrawGdoPanel(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiGdoPanel);
            var elements = new CuiElementContainer();

            string panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.04 0.06 0.08 0.96", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0.33 0.3", AnchorMax = "0.67 0.7" },
                CursorEnabled = true
            }, "Overlay", UiGdoPanel);

            double now = Interface.Oxide.Now;
            string cooldownText = "Hazır";
            if (_gdoCooldownUntil.TryGetValue(player.userID, out double until) && until > now)
            {
                cooldownText = $"{Mathf.CeilToInt((float)(until - now))} sn";
            }

            elements.Add(new CuiLabel
            {
                Text = { Text = $"GDO PANEL\n<size=13>Cooldown: {cooldownText}</size>", Align = TextAnchor.UpperCenter, FontSize = 20, Color = "1 1 1 1", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.05 0.78", AnchorMax = "0.95 0.98" }
            }, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Örnek: /gdo claim hemp GGGGYY\nGen: G,Y,H,W,X (6 karakter)", Align = TextAnchor.MiddleCenter, FontSize = 12, Color = "0.75 0.85 1 1" },
                RectTransform = { AnchorMin = "0.05 0.56", AnchorMax = "0.95 0.76" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.0 0.6 0.9 1", Command = "gdo.claim hemp GGGGYY" },
                RectTransform = { AnchorMin = "0.08 0.4", AnchorMax = "0.92 0.5" },
                Text = { Text = "HEMP GGGGYY AL", FontSize = 14, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.0 0.6 0.9 1", Command = "gdo.claim corn GYYGGY" },
                RectTransform = { AnchorMin = "0.08 0.27", AnchorMax = "0.92 0.37" },
                Text = { Text = "CORN GYYGGY AL", FontSize = 14, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.0 0.6 0.9 1", Command = "gdo.claim pumpkin GGHHYY" },
                RectTransform = { AnchorMin = "0.08 0.14", AnchorMax = "0.92 0.24" },
                Text = { Text = "PUMPKIN GGHHYY AL", FontSize = 14, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "gdo.close" },
                RectTransform = { AnchorMin = "0.72 0.03", AnchorMax = "0.92 0.11" },
                Text = { Text = "KAPAT", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        private int GetCategoryTotalLevel(PlayerData data, SkillCategory category)
        {
            int total = 0;
            foreach (var skill in category.Skills)
            {
                if (data.Skills.TryGetValue(skill.Id, out int lvl))
                    total += lvl;
            }
            return total;
        }

        private void UpgradeSkill(BasePlayer player, string skillId)
        {
            var data = GetPlayer(player.userID);
            var category = _config.Categories.FirstOrDefault(c => c.Skills.Any(s => s.Id == skillId));
            if (category == null) return;
            var skillDef = category.Skills.FirstOrDefault(s => s.Id == skillId);

            if (skillDef == null || data.SkillPoints <= 0) return;

            int categoryTotalLvl = GetCategoryTotalLevel(data, category);
            if (categoryTotalLvl < skillDef.RequiredCategoryLevel)
            {
                player.ChatMessage($"<color=#ff5555>Bu yeteneği açmak için '{category.DisplayName}' kategorisinde toplam {skillDef.RequiredCategoryLevel} seviye harcamış olmalısınız!</color>");
                return;
            }

            data.Skills.TryGetValue(skillId, out int currentLevel);
            if (currentLevel >= skillDef.MaxLevel) return;

            data.Skills[skillId] = currentLevel + 1;
            data.SkillPoints--;
            
            // Hangi sekmedeysek onu tekrar çiz
            DrawUI(player, category.Id);
        }

        private void DrawUI(BasePlayer player, string activeTabId)
        {
            CuiHelper.DestroyUi(player, UiMainPanel);
            var data = GetPlayer(player.userID);
            var elements = new CuiElementContainer();

            // 1. Ana Arka Plan (Modern karanlık tema)
            elements.Add(new CuiPanel {
                Image = { Color = "0.06 0.07 0.09 0.98", Material = "assets/content/ui/uibackgroundblur.mat" }, // Hafif blur + koyu lacivert/siyah
                RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" },
                CursorEnabled = true
            }, "Overlay", UiMainPanel);

            // 2. Sol Navigasyon Menüsü (Daha koyu sidebar)
            string navPanel = elements.Add(new CuiPanel {
                Image = { Color = "0.03 0.04 0.05 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.22 1" }
            }, UiMainPanel);

            // Oyuncu Statları (Sol Üst - Genişletildi)
            float xpNeeded = GetXpRequirement(data.Level);
            int pointsSpent = Math.Max(0, (data.Level - 1) - data.SkillPoints);

            string statsText = $"<size=28>SEVİYE {data.Level}</size>\n" +
                               $"<color=#00e5ff>Mevcut Puan: {data.SkillPoints}</color>\n" +
                               $"<color=#a0a0a0><size=11>Harcanan Puan: {pointsSpent}</size>\n" +
                               $"<size=11>Mevcut XP: {Mathf.Floor(data.TotalXp)} / {Mathf.Floor(xpNeeded)}</size></color>";

            elements.Add(new CuiLabel {
                Text = { Text = statsText, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.78", AnchorMax = "1 0.98" } // Alan aşağıya doğru genişletildi
            }, navPanel);

            // Sekmeleri Çiz (Modern listeleme stili)
            float tabY = 0.75f; // Tablar metne yer açmak için biraz aşağı indirildi
            foreach (var category in _config.Categories)
            {
                bool isActive = category.Id == activeTabId;
                string btnColor = isActive ? "0.12 0.14 0.18 1" : "0 0 0 0";
                string txtColor = isActive ? "0.0 0.9 1.0 1.0" : "0.5 0.5 0.5 1.0"; // Aktif sekme parlak cyan

                elements.Add(new CuiButton {
                    Button = { Command = $"skilltree.action tab {category.Id}", Color = btnColor },
                    RectTransform = { AnchorMin = $"0.0 {tabY - 0.08f}", AnchorMax = $"1.0 {tabY}" },
                    Text = { Text = category.DisplayName, FontSize = 18, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = txtColor }
                }, navPanel);

                // Aktif sekme için sol belirteç bar (Accent bar)
                if (isActive)
                {
                    elements.Add(new CuiPanel {
                        Image = { Color = "0.0 0.9 1.0 1" },
                        RectTransform = { AnchorMin = $"0.0 {tabY - 0.08f}", AnchorMax = $"0.02 {tabY}" }
                    }, navPanel);
                }
                
                tabY -= 0.08f;
            }

            // 4. Sağ İçerik Alanı (Seçili Sekmenin Yetenekleri)
            string contentPanel = elements.Add(new CuiPanel {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.24 0", AnchorMax = "1 1" }
            }, UiMainPanel);

            var activeCategory = _config.Categories.FirstOrDefault(c => c.Id == activeTabId) ?? _config.Categories.First();
            
            // Seçili Kategori Başlığı
            elements.Add(new CuiLabel {
                Text = { Text = activeCategory.DisplayName.ToUpper(), FontSize = 36, Font = "robotocondensed-bold.ttf", Align = TextAnchor.LowerLeft, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0.02 0.85", AnchorMax = "1 0.93" }
            }, contentPanel);

            // Alt tire (Ayırıcı Çizgi)
            elements.Add(new CuiPanel {
                Image = { Color = "1 1 1 0.1" },
                RectTransform = { AnchorMin = "0.02 0.84", AnchorMax = "0.96 0.845" }
            }, contentPanel);

            // Yetenek Kartlarını Çiz (4x3 ızgara sığacak şekilde küçültüldü)
            float startX = 0.02f; float startY = 0.58f;
            int categoryTotalLvl = GetCategoryTotalLevel(data, activeCategory);

            foreach (var skill in activeCategory.Skills)
            {
                data.Skills.TryGetValue(skill.Id, out int currentLvl);
                bool isLocked = categoryTotalLvl < skill.RequiredCategoryLevel;
                DrawSkillCard(elements, contentPanel, skill, currentLvl, isLocked, startX, startY);
                
                startX += 0.24f; // Yatay boşluk
                if (startX > 0.9f) { startX = 0.02f; startY -= 0.26f; } // Alt satıra in
            }

            // Kapatma Butonu (En sona ekliyoruz ki diğer tüm z-index katmanlarının üzerinde kalsın ve raycastı engellenmesin)
            elements.Add(new CuiButton {
                Button = { Command = "skilltree.action close", Color = "0.8 0.15 0.15 1" }, // Modern kırmızı
                RectTransform = { AnchorMin = "0.90 0.92", AnchorMax = "0.98 0.98" },
                Text = { Text = "KAPAT", FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiMainPanel);

            CuiHelper.AddUi(player, elements);
        }

        // Bireysel Yetenek Kartı ve İkonu (Modern, yuvarlatılmış hissiyatı veren düz renkler)
private void DrawSkillCard(CuiElementContainer container, string parent, SkillDefinition skill, int currentLvl, bool isLocked, float x, float y)
        {
            string cardColor = isLocked ? "0.08 0.08 0.08 0.9" : "0.12 0.14 0.18 1";
            string cardName = container.Add(new CuiPanel {
                Image = { Color = cardColor }, // Koyu mavi-gri veya kilitli kart zemini
                RectTransform = { AnchorMin = $"{x} {y}", AnchorMax = $"{x + 0.23f} {y + 0.24f}" }
            }, parent);

            // İkonu Sola Daya
            if (!string.IsNullOrEmpty(skill.IconUrl))
            {
                container.Add(new CuiElement
                {
                    Parent = cardName,
                    Components = {
                        new CuiRawImageComponent { Url = skill.IconUrl, Sprite = "assets/content/textures/generic/fulltransparent.tga", Color = isLocked ? "0.3 0.3 0.3 1" : "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.35", AnchorMax = "0.33 0.85" }
                    }
                });
            }

            // Gelişim Çubuğu (Progres Bar) kartın üst kısmında
            float skillProgress = (float)currentLvl / skill.MaxLevel;
            container.Add(new CuiPanel { Image = { Color = "0 0 0 0.5" }, RectTransform = { AnchorMin = "0 0.95", AnchorMax = "1 1" } }, cardName);
            container.Add(new CuiPanel { Image = { Color = "0.0 0.9 1.0 1.0" }, RectTransform = { AnchorMin = "0 0.95", AnchorMax = $"{skillProgress} 1" } }, cardName);

            // Yetenek İsmi
            string titleText = skill.DisplayName + (isLocked ? " \n<color=#ff5555><size=10>(Kilitli)</size></color>" : "");
            container.Add(new CuiLabel {
                Text = { Text = titleText, FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.LowerLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.36 0.60", AnchorMax = "0.98 0.90" }
            }, cardName);

            // Açıklama (Gri metin)
            container.Add(new CuiLabel {
                Text = { Text = skill.Description, FontSize = 10, Align = TextAnchor.UpperLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.36 0.30", AnchorMax = "0.98 0.60" }
            }, cardName);

            // Upgrade Butonu (Modern Düz Renkler)
            bool isMax = currentLvl >= skill.MaxLevel;
            string btnColor = isLocked ? "0.2 0.2 0.2 1" : (isMax ? "0.15 0.5 0.25 1" : "0.0 0.6 0.9 1");
            string btnText = isLocked ? $"AÇMAK İÇİN {skill.RequiredCategoryLevel} PUAN" : (isMax ? "MAX SEVİYE" : $"GELİŞTİR ({currentLvl}/{skill.MaxLevel})");

            container.Add(new CuiButton {
                Button = { Command = (isMax || isLocked) ? "" : $"skilltree.action upgrade {skill.Id}", Color = btnColor },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.24" },
                Text = { Text = btnText, FontSize = 11, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, cardName);
        }

        // Kalıcı HUD Barı (Can barının hemen üstüne konumlanmış XP ve Level göstergesi)
        private void DrawHUD(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiHudPanel);
            var data = GetPlayer(player.userID);
            
            float xpNeeded = GetXpRequirement(data.Level);
            float progress = Mathf.Clamp01(data.TotalXp / xpNeeded);
            
            var elements = new CuiElementContainer();

            // Daha modern, net bir arayüz (Hafif siyah blur)
            string hudBg = elements.Add(new CuiPanel {
                Image = { Color = "0.05 0.06 0.08 0.95", Material = "assets/content/ui/uibackgroundblur.mat" }, 
                RectTransform = { AnchorMin = "0.73 0.12", AnchorMax = "0.83 0.16" } // Envanter/Can barlarının üst hizası
            }, "Hud", UiHudPanel);

            // Modern Cyan/Mavi Progress Bar (Alt kısımda çizgi gibi veya yarı dolu bar)
            elements.Add(new CuiPanel {
                Image = { Color = "0.0 0.8 1.0 0.85" }, // Cyan mavisi
                RectTransform = { AnchorMin = $"0 0", AnchorMax = $"{progress} 0.12" } // İnce ve modern bir bar
            }, hudBg);

            // Level Text
            elements.Add(new CuiLabel {
                Text = { Text = $"LVL <color=#00e5ff>{data.Level}</color>", FontSize = 13, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0", AnchorMax = "0.4 1" }
            }, hudBg);

            // XP Text
            elements.Add(new CuiLabel {
                Text = { Text = $"<size=9>{Mathf.Floor(data.TotalXp)} / {xpNeeded} XP</size>", Align = TextAnchor.MiddleRight, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.4 0", AnchorMax = "0.95 1" }
            }, hudBg);

            CuiHelper.AddUi(player, elements);
        }

        #endregion
    }
}