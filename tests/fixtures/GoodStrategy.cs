#region Using declarations
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class GoodStrategy : Strategy
    {
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "GoodStrategy";
                Calculate = Calculate.OnBarClose;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20) return;
            if (Close[0] > Close[1]) EnterLong();
        }
    }
}
