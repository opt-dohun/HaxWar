namespace HexWar.Benchmarks;

using NUnit.Framework;

// 전체 테스트 실행 명령어 dotnet test tests/HexWar.Benchmarks/HexWar.Benchmarks.csproj
// 테스트 실행 명령어 dotnet test tests/HexWar.Benchmarks/HexWar.Benchmarks.csproj --filter FullyQualifiedName~SimulationValidationTests

[TestFixture]
public class SimulationValidationTests
{
    // 디버그 메시지(상세 이동 로그)를 콘솔에 출력할지 여부를 선택하는 플래그
    private const bool EnableVerboseLogging = false;

    [Test]
    [Repeat(10)]
    // LessThanOrEqualTo => N 인자 이하인지 검증 
    // GreaterThan => N 인자 이상인지 검증
    // That => A 검증 대상 값 , B 비교 대상 값 , 옵션: C 실패 메시지 
    public void Simulation_ShouldNotViolateGameRules()
    {
        var sim = new RealisticGameSimulator(verbose: EnableVerboseLogging);
        var result = sim.SimulateCompleteGame();

        TestContext.Progress.WriteLine($"=== Game Result ===");
        TestContext.Progress.WriteLine($"Rounds: {result.TotalRounds}");
        TestContext.Progress.WriteLine($"Encounters: {result.EncounterCount}");
        TestContext.Progress.WriteLine($"Moves A: {result.TotalMovesA}, Moves B: {result.TotalMovesB}");
        TestContext.Progress.WriteLine($"Winner: {result.Winner}");
        TestContext.Progress.WriteLine($"Scores: A={result.FinalScores["A"]}, B={result.FinalScores["B"]}");

        // 검증
        Assert.That(result.TotalRounds, Is.GreaterThan(0), "Game must have at least 1 round");
        Assert.That(result.TotalRounds, Is.LessThanOrEqualTo(21), "Game cannot exceed 20 rounds + final");

        // 이동 횟수 검증 (라운드당 최대 3기)
        Assert.That(result.TotalMovesA, Is.LessThanOrEqualTo(result.TotalRounds * 3));
        Assert.That(result.TotalMovesB, Is.LessThanOrEqualTo(result.TotalRounds * 3));

        // 점수 합계 검증 (본부 제외 5개 노드)
        Assert.That(result.FinalScores["A"] + result.FinalScores["B"], Is.LessThanOrEqualTo(5));
    }

    /// <summary>
    /// 100게임 실제 실행 시 통계 정보 출력 및 실행 테스트
    /// </summary>
    [Test]
    public void Simulate100Games_Statistics()
    {
        var results = new List<GameSimulationResult>();

        for (int i = 0; i < 100; i++)
        {
            var sim = new RealisticGameSimulator(seed: 42 + i);
            results.Add(sim.SimulateCompleteGame());
        }

        var avgRounds = results.Average(r => r.TotalRounds);
        var avgEncounters = results.Average(r => r.EncounterCount);
        var winsA = results.Count(r => r.Winner == "A");
        var winsB = results.Count(r => r.Winner == "B");
        var draws = results.Count(r => r.Winner == "Draw");

        TestContext.Progress.WriteLine($"=== 100 Games Statistics ===");
        TestContext.Progress.WriteLine($"Avg Rounds: {avgRounds:F1}");
        TestContext.Progress.WriteLine($"Avg Encounters: {avgEncounters:F1}");
        TestContext.Progress.WriteLine($"Win Rates: A={winsA}%, B={winsB}%, Draw={draws}%");
        TestContext.Progress.WriteLine($"Game Over by MaxRounds: {results.Count(r => r.TotalRounds >= 20)}");
    }

    /// <summary>
    /// GC 세대별 컬렉션 횟수를 추적하며 장시간 게임 시뮬레이션
    /// 한번의 게임 실행을 통해 검증
    /// </summary>
    [Test]
    public void LongRunningGame_GCAnalysis()
    {
        var sim = new RealisticGameSimulator(verbose: EnableVerboseLogging);
        var result = sim.SimulateCompleteGame(true);

        foreach (var snap in result.GCSnapshots)
        {
            TestContext.WriteLine(
                $"{snap.Round,-8} {snap.Gen0,-8} {snap.Gen1,-8} {snap.Gen2,-8} {snap.TotalMemory / 1024.0,-12:F1}");
        }

        // 1) 통계 출력
        TestContext.Progress.WriteLine($"=== GC Analysis ===");
        TestContext.Progress.WriteLine($"Total Rounds: {result.TotalRounds}");
        TestContext.Progress.WriteLine($"Total GC Collections: {result.GCSnapshots.Sum(s => s.Gen0 + s.Gen1 + s.Gen2)}");

        // 2) 세대별 총 컬렉션 횟수
        int totalGen0 = result.GCSnapshots.Sum(s => s.Gen0);
        int totalGen1 = result.GCSnapshots.Sum(s => s.Gen1);
        int totalGen2 = result.GCSnapshots.Sum(s => s.Gen2);
        long totalMemory = result.GCSnapshots.Max(s => s.TotalMemory);

        TestContext.Progress.WriteLine($"Gen 0: {totalGen0} times");
        TestContext.Progress.WriteLine($"Gen 1: {totalGen1} times");
        TestContext.Progress.WriteLine($"Gen 2: {totalGen2} times");
        TestContext.Progress.WriteLine($"Max Memory: {totalMemory / 1024 / 1024} MB");
    }


}