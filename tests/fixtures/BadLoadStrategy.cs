#region Using declarations
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BadLoadStrategy : Strategy
    {
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BadLoadStrategy";
                // Illegal in SetDefaults: surfaces only when NT8 instantiates the type.
                BacktestCommissionTemplate = "DoesNotExist";
            }
        }
    }
}
