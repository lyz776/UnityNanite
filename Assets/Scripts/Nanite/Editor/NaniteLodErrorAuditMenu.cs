#if UNITY_EDITOR
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    /// <summary>
    /// 审计 NaniteMesh bake 的 selfError/parentError，重点统计 float.MaxValue。
    /// </summary>
    public static class NaniteLodErrorAuditMenu
    {
        const float kMaxValueThreshold = 1e20f;

        [MenuItem("Nanite/Audit LOD Errors (Selected NaniteMesh)")]
        static void AuditSelected()
        {
            var mesh = Selection.activeObject as NaniteMesh;
            if (mesh == null)
            {
                Debug.LogWarning("[Nanite][LOD Audit] 请先在 Project 选中 NaniteMesh（如 toyota_ft1_mesh）。");
                return;
            }

            Debug.Log(AuditMesh(mesh));
        }

        [MenuItem("Nanite/Audit LOD Errors (toyota_ft1_mesh)")]
        static void AuditToyota()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>("Assets/toyota_ft1_mesh.asset");
            if (mesh == null)
            {
                Debug.LogWarning("[Nanite][LOD Audit] 未找到 Assets/toyota_ft1_mesh.asset");
                return;
            }

            Selection.activeObject = mesh;
            Debug.Log(AuditMesh(mesh));
        }

        public static string AuditMesh(NaniteMesh mesh)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine($"[Nanite][LOD Audit] mesh={mesh.name} pages={mesh.pageArray?.Length ?? 0} maxMip={mesh.maxMipLevel}");

            int totalClusters = 0;
            int parentMax = 0;
            int selfMax = 0;
            int bothFinite = 0;
            int leafSelfZero = 0;
            int parentMaxSelfZero = 0;
            float minParent = float.PositiveInfinity;
            float maxParentFinite = 0f;
            float minSelf = float.PositiveInfinity;
            float maxSelfFinite = 0f;
            int partsTotal = 0;
            int partsMaxParent = 0;
            int bvhNodes = 0;
            int pagesWithBvh = 0;

            if (mesh.pageArray == null)
            {
                sb.AppendLine("pageArray=null");
                return sb.ToString();
            }

            for (int p = 0; p < mesh.pageArray.Length; p++)
            {
                var page = mesh.pageArray[p];
                if (page == null)
                {
                    sb.AppendLine($"  page[{p}]=null");
                    continue;
                }

                int pageParentMax = 0;
                int pageClusters = page.clusterArray != null ? page.clusterArray.Length : 0;
                if (page.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                {
                    pagesWithBvh++;
                    bvhNodes += page.bvhNodes.Length;
                }

                if (page.parts != null)
                {
                    partsTotal += page.parts.Length;
                    for (int i = 0; i < page.parts.Length; i++)
                    {
                        if (page.parts[i].maxParentLodError >= kMaxValueThreshold)
                            partsMaxParent++;
                    }
                }

                if (page.clusterArray == null)
                    continue;

                for (int c = 0; c < page.clusterArray.Length; c++)
                {
                    var cl = page.clusterArray[c];
                    totalClusters++;
                    bool pMax = cl.parentError >= kMaxValueThreshold || float.IsInfinity(cl.parentError);
                    bool sMax = cl.selfError >= kMaxValueThreshold || float.IsInfinity(cl.selfError);
                    if (pMax) { parentMax++; pageParentMax++; }
                    if (sMax) selfMax++;
                    if (!pMax && !sMax) bothFinite++;
                    if (cl.selfError <= 1e-12f) leafSelfZero++;
                    if (pMax && cl.selfError <= 1e-12f) parentMaxSelfZero++;

                    if (!pMax)
                    {
                        minParent = Mathf.Min(minParent, cl.parentError);
                        maxParentFinite = Mathf.Max(maxParentFinite, cl.parentError);
                    }

                    if (!sMax)
                    {
                        minSelf = Mathf.Min(minSelf, cl.selfError);
                        maxSelfFinite = Mathf.Max(maxSelfFinite, cl.selfError);
                    }
                }

                sb.AppendLine(
                    $"  page[{p}]={page.name} clusters={pageClusters} " +
                    $"parentError=MaxValue:{pageParentMax} ({Pct(pageParentMax, pageClusters)}) " +
                    $"bvh={(page.bvhRoot >= 0 ? page.bvhNodes?.Length ?? 0 : 0)}");
            }

            sb.AppendLine("--- totals ---");
            sb.AppendLine($"clusters={totalClusters}");
            sb.AppendLine($"parentError=MaxValue: {parentMax} ({Pct(parentMax, totalClusters)})  ← bake 卡住简化/根节点会写这个");
            sb.AppendLine($"selfError=MaxValue: {selfMax} ({Pct(selfMax, totalClusters)})");
            sb.AppendLine($"selfError≈0 (leaf): {leafSelfZero} ({Pct(leafSelfZero, totalClusters)})");
            sb.AppendLine($"parent=MaxValue & self=0: {parentMaxSelfZero} ({Pct(parentMaxSelfZero, totalClusters)})  ← 永不因 parent 阈值被切掉");
            sb.AppendLine($"both finite: {bothFinite} ({Pct(bothFinite, totalClusters)})");
            sb.AppendLine(
                $"finite parentError range: [{F(minParent)}, {F(maxParentFinite)}]  " +
                $"finite selfError range: [{F(minSelf)}, {F(maxSelfFinite)}]");
            sb.AppendLine($"parts={partsTotal}, parts.maxParentLodError=MaxValue: {partsMaxParent} ({Pct(partsMaxParent, partsTotal)})");
            sb.AppendLine($"pagesWithBvh={pagesWithBvh}/{mesh.pageArray.Length}, bvhNodes={bvhNodes}");
            sb.AppendLine();
            sb.AppendLine("解读:");
            sb.AppendLine("- 互斥 LOD：同一处只应选一层。宽重叠带会双选父子→浮动碎块（已禁用）。");
            sb.AppendLine("- parentError=MaxValue：无法退化；比例过高则远处一直密。");
            sb.AppendLine("- parentError 过小：近处也会切到粗层（「不够精细」）。Bake 现用半径×2% 作下限，改完需重 Bake。");
            sb.AppendLine("- lodErrorPixels：1~2 看质量；8 只适合压测掉 cluster，近处变糙是正常的。");
            sb.AppendLine("- Feature 驱动看 lastGpuVisibleApprox。");
            return sb.ToString();
        }

        static string Pct(int n, int total)
        {
            if (total <= 0) return "0%";
            return (100f * n / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        static string F(float v)
        {
            if (float.IsPositiveInfinity(v)) return "inf";
            if (float.IsNegativeInfinity(v)) return "-inf";
            return v.ToString("G6", CultureInfo.InvariantCulture);
        }
    }
}
#endif
