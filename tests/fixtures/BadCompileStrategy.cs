#region Using declarations
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BadCompileStrategy : Strategy
    {
        protected override void OnBarUpdate()
        {
            int x = foo;       // CS0103: name 'foo' does not exist in the current context
            int y = bar;       // CS0103: name 'bar' does not exist in the current context
        }
    }
}
