namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 手冊索引與現行組態不相容（規格 §10.7）。典型成因是換了嵌入模型卻沒重建索引。
///
/// <para><b>為什麼要當成錯誤而不是自動降級</b>：向量空間不可比這件事沒有任何外顯訊號——
/// 檢索照樣回得出 Top-K，分數也是合法的浮點數，只是排序完全是雜訊。
/// 若在這裡「盡量用」，症狀會是「助理答非所問」，而沒有人會想到去看嵌入模型的組態。</para>
///
/// <para>失敗方向刻意選成吵鬧的那一邊：拒絕檢索、講清楚兩邊分別是什麼、要求重建索引。</para>
/// </summary>
public class DocIndexOutOfDateException : Exception
{
    public DocIndexOutOfDateException(string message) : base(message) { }
}
