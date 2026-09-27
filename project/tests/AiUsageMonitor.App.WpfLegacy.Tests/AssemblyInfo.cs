// WPFのSTA UIテストは、複数Windowを並列表示するとアクティブ化・モーダル(ShowDialog)と競合して不安定になる。
// アセンブリ全体でテスト並列を無効化し、Window表示テストを直列実行する。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
