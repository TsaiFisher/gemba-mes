using System;
using System.Collections.Generic;
using System.Linq;
using SmartFactory.MES.Modules.QCR.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QCR
{
    /// <summary>SpcCalculator（Cp/Cpk/σ/UCL-LCL/超規）純函式驗證。</summary>
    public class SpcCalculatorTests
    {
        [Fact]
        public void Empty_returns_zeros_and_null_capability()
        {
            var s = SpcCalculator.Compute(Array.Empty<decimal>(), 0m, 10m);
            Assert.Equal(0, s.Count);
            Assert.Null(s.Cp);
            Assert.Null(s.Cpk);
            Assert.Equal(0, s.OutOfSpecCount);
        }

        [Fact]
        public void Single_value_has_no_sigma_no_capability()
        {
            var s = SpcCalculator.Compute(new[] { 5m }, 0m, 10m);
            Assert.Equal(1, s.Count);
            Assert.Equal(5m, s.Mean);
            Assert.Equal(0m, s.StdDev);     // n<2 → σ=0
            Assert.Null(s.Cp);              // σ=0 → 不可計算
            Assert.Null(s.Cpk);
        }

        [Fact]
        public void Mean_min_max_computed()
        {
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, null, null);
            Assert.Equal(4m, s.Mean);
            Assert.Equal(2m, s.Min);
            Assert.Equal(6m, s.Max);
        }

        [Fact]
        public void Sample_stddev_uses_n_minus_1()
        {
            // 值 {2,4,6}：mean=4，SS=8，/(n-1)=/2=4 → σ=2
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, null, null);
            Assert.Equal(2m, s.StdDev);
            Assert.Equal(4m + 3m * 2m, s.Ucl);   // mean+3σ = 10
            Assert.Equal(4m - 3m * 2m, s.Lcl);   // mean-3σ = -2
        }

        [Fact]
        public void Cp_and_Cpk_centered_process()
        {
            // {2,4,6}: mean=4, σ=2; LSL=-2, USL=10 → Cp=(10-(-2))/(6*2)=12/12=1
            // Cpu=(10-4)/(3*2)=1; Cpl=(4-(-2))/(3*2)=1 → Cpk=1
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, -2m, 10m);
            Assert.Equal(1.00m, s.Cp);
            Assert.Equal(1.00m, s.Cpk);
        }

        [Fact]
        public void Cpk_less_than_Cp_when_off_center()
        {
            // {2,4,6}: mean=4, σ=2; LSL=0, USL=10 → Cp=10/12≈0.8333
            // Cpu=(10-4)/6=1; Cpl=(4-0)/6≈0.6667 → Cpk=0.6667 < Cp
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, 0m, 10m);
            Assert.Equal(0.8333m, s.Cp);
            Assert.Equal(0.6667m, s.Cpk);
            Assert.True(s.Cpk < s.Cp);
        }

        [Fact]
        public void Single_sided_upper_only_gives_Cpu_no_Cp()
        {
            // 只有 USL：Cp 不可計算，Cpk = Cpu
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, null, 10m);
            Assert.Null(s.Cp);
            Assert.Equal(1.00m, s.Cpk);   // (10-4)/(3*2)
        }

        [Fact]
        public void Single_sided_lower_only_gives_Cpl_no_Cp()
        {
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, 0m, null);
            Assert.Null(s.Cp);
            Assert.Equal(0.6667m, s.Cpk); // (4-0)/(3*2)
        }

        [Fact]
        public void OutOfSpec_counts_values_beyond_limits()
        {
            // LSL=3, USL=5：{2,4,6} → 2(<3) 與 6(>5) 超規 = 2
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, 3m, 5m);
            Assert.Equal(2, s.OutOfSpecCount);
        }

        [Fact]
        public void OutOfSpec_respects_single_bound()
        {
            // 只有 USL=5：{2,4,6} → 只 6 超規 = 1
            var s = SpcCalculator.Compute(new[] { 2m, 4m, 6m }, null, 5m);
            Assert.Equal(1, s.OutOfSpecCount);
        }

        [Fact]
        public void Zero_variance_data_no_capability_but_counts_oos()
        {
            // 全相同值 → σ=0 → Cp/Cpk null；但仍可判超規
            var s = SpcCalculator.Compute(new[] { 8m, 8m, 8m }, 0m, 5m);
            Assert.Equal(0m, s.StdDev);
            Assert.Null(s.Cp);
            Assert.Null(s.Cpk);
            Assert.Equal(3, s.OutOfSpecCount);   // 8>5 三次
        }
    }
}
