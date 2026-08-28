using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Lumina.Excel.Sheets;

    internal class PaladinWeaponCofferHelper : ActiveHelperBase<PaladinWeaponCofferHelper>
    {
        public override string[]? Commands { get; init; } = ["paladinweaponcoffer"];
        public override string? CommandDescription { get; init; } = "Opens Paladin's Weapon coffers in your inventory";

        private readonly Dictionary<uint, int> doneItems = [];

        internal override void Start()
        {
            base.Start();
            this.doneItems.Clear();
        }

        protected override string Name        { get; } = nameof(PaladinWeaponCofferHelper);
        protected override string DisplayName { get; } = "Opening Paladin's Weapon Coffers";

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (!this.UpdateBase())
                return;

            if (Conditions.Instance()->Mounted)
            {
                this.DebugLog("Dismount");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
                return;
            }

            if (InventoryManager.Instance()->GetEmptySlotsInBag() < 1)
            {
                this.DebugLog("No empty slots");
                this.Stop();
                return;
            }

            if (PlayerHelper.IsCasting || !PlayerHelper.IsReadyFull || Player.IsBusy)
                return;

            this.DebugLog("Checking items");

            IEnumerable<InventoryItem> items = InventoryHelper.GetInventorySelection(InventoryHelper.Bag)
                                                               .Where(iv =>
                                                                      {
                                                                          Item? excelItem = InventoryHelper.GetExcelItem(iv.ItemId);
                                                                          this.DebugLog($"checking item: {iv.ItemId} in {iv.Container} {iv.Slot}");
                                                                          return iv.ItemId > 0 && (!this.doneItems.ContainsKey(iv.ItemId) || this.doneItems[iv.ItemId] != iv.Quantity) && excelItem.HasValue && ValidCoffer(excelItem.Value);
                                                                      });

            if (items.Any())
            {
                this.DebugLog("item found");

                InventoryItem item = items.First();

                InventoryHelper.UseItem(item.ItemId);

                if (!PlayerHelper.IsCasting)
                {
                    this.DebugLog("failed to use item");
                    return;
                }

                this.DebugLog("item used");
                this.doneItems[item.ItemId] = item.Quantity;
            }
            else
            {
                this.DebugLog("no items found");
                this.Stop();
            }
        }

        internal static bool ValidCoffer(Item item)
        {
            if (Data.PaladinWeaponCofferItems.RowIds.Contains(item.RowId))
                return true;

            if (item.RowId <= Data.PaladinWeaponCofferItems.MaxKnownRowId)
                return false;

            if (item.ItemAction.RowId is not (1085 or 388 or 367) || item.ItemUICategory.RowId is not 61)
                return false;

            Lumina.Data.Language language = Svc.Data.GetExcelSheet<Item>()!.Language;

            return Data.PaladinWeaponCofferItems.NamePatternsByLanguage.TryGetValue(language, out Regex? regex) &&
                   regex.IsMatch(item.Name.ToString());
        }
    }
}
