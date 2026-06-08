using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Guards the CPU-architecture heuristics that drive core-affinity decisions (P/E split, X3D, etc.).
    public class CoreDirectorTests
    {
        [Theory]
        [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", CpuArchitecture.IntelHybrid)]
        [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", CpuArchitecture.IntelHybrid)]
        [InlineData("Intel(R) Core(TM) i5-14600KF", CpuArchitecture.IntelHybrid)]
        [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuArchitecture.IntelHybrid)]
        [InlineData("Intel(R) Core(TM) i7-10700K", CpuArchitecture.IntelStandard)] // 10th gen, no P/E
        [InlineData("Intel(R) Core(TM) i7-1165G7", CpuArchitecture.IntelStandard)] // 11th gen mobile
        [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", CpuArchitecture.AmdX3D)]
        [InlineData("AMD Ryzen 5 5600 6-Core Processor", CpuArchitecture.AmdStandard)]
        [InlineData("Some Unknown CPU", CpuArchitecture.Other)]
        [InlineData("", CpuArchitecture.Unknown)]
        public void DetectArchitecture_ClassifiesKnownModels(string model, CpuArchitecture expected)
        {
            Assert.Equal(expected, CoreDirector.DetectArchitecture(model));
        }
    }
}
