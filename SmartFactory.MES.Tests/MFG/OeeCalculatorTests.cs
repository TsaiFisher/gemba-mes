using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG
{
    /// <summary>OeeCalculator（可用率／表現性／良率／OEE）純函式驗證。</summary>
    public class OeeCalculatorTests
    {
        [Fact]
        public void Availability_is_run_over_planned()
        {
            Assert.Equal(0.8m, OeeCalculator.Availability(500m, 400m));
        }

        [Fact]
        public void Performance_is_ideal_over_run()
        {
            Assert.Equal(0.75m, OeeCalculator.Performance(400m, 300m));
        }

        [Fact]
        public void Quality_is_good_over_total()
        {
            Assert.Equal(0.95m, OeeCalculator.Quality(100m, 95m));
        }

        [Fact]
        public void Zero_denominators_return_zero()
        {
            Assert.Equal(0m, OeeCalculator.Availability(0m, 400m));
            Assert.Equal(0m, OeeCalculator.Performance(0m, 300m));
            Assert.Equal(0m, OeeCalculator.Quality(0m, 95m));
        }

        [Fact]
        public void Ratios_clamp_at_one()
        {
            // 資料雜訊致 run>planned / ideal>run / good>total → 上限截頂為 1
            Assert.Equal(1m, OeeCalculator.Availability(400m, 500m));
            Assert.Equal(1m, OeeCalculator.Performance(300m, 400m));
            Assert.Equal(1m, OeeCalculator.Quality(95m, 100m));
        }

        [Fact]
        public void Compute_multiplies_three_factors()
        {
            // A=400/500=0.8、P=300/400=0.75、Q=95/100=0.95 → OEE=0.8*0.75*0.95=0.57
            var r = OeeCalculator.Compute(500m, 400m, 300m, 100m, 95m);
            Assert.Equal(0.8m,  r.Availability);
            Assert.Equal(0.75m, r.Performance);
            Assert.Equal(0.95m, r.Quality);
            Assert.Equal(0.57m, r.Oee);
        }

        [Fact]
        public void Compute_all_zero_when_no_data()
        {
            var r = OeeCalculator.Compute(0m, 0m, 0m, 0m, 0m);
            Assert.Equal(0m, r.Availability);
            Assert.Equal(0m, r.Performance);
            Assert.Equal(0m, r.Quality);
            Assert.Equal(0m, r.Oee);
        }
    }
}
