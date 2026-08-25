namespace CollectionServer.Options;

public class AnomalyDetectionOptions
{
    public const string SectionName = "AnomalyDetection";

    /// <summary>관측값이 기준선(baseline) 평균 대비 이 배수를 초과하면 이상으로 표기</summary>
    public double ThresholdMultiplier { get; set; } = 3.0;

    /// <summary>기준선 계산에 필요한 최소 과거 샘플 수 (부족하면 해당 경로는 응답시간 기준 판단에서 제외)</summary>
    public int MinBaselineSamples { get; set; } = 5;

    /// <summary>
    /// 하트비트처럼 원래 1ms 미만인 초고속 작업은 미세한 지터만으로도 배수 임계값을 쉽게 넘어서
    /// 노이즈가 발생함 — 배수 조건과 별개로 절대 지연 증가량이 이 값(ms) 이상이어야 이상으로 표기.
    /// </summary>
    public double MinAbsoluteDeltaMs { get; set; } = 5.0;
}
