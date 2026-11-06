using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using System.Windows.Forms;
using ThreadPro;

namespace ThreadPro.ThreadMetadataTest
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            string sourceRoot = ResolveSourceRoot();
            int pass = 0;
            int fail = 0;

            string runtimeAuditGateTestName = @"Local\Helivanta.RuntimeAudit.PolicyTest." + Guid.NewGuid().ToString("N");
            Check("RUNTIME-AUDIT-GATE-IS-CLOSED-WITHOUT-AN-OPT-IN-EVENT",
                !RuntimeAuditAccessGate.IsEnabled(runtimeAuditGateTestName), ref pass, ref fail,
                "The runtime-audit gate must fail closed when no named opt-in event exists.");
            using (EventWaitHandle runtimeAuditTestGate = new EventWaitHandle(false, EventResetMode.ManualReset,
                runtimeAuditGateTestName, out bool runtimeAuditGateCreated))
            {
                bool closedBeforeSet = !RuntimeAuditAccessGate.IsEnabled(runtimeAuditGateTestName);
                runtimeAuditTestGate.Set();
                bool openAfterSet = RuntimeAuditAccessGate.IsEnabled(runtimeAuditGateTestName);
                runtimeAuditTestGate.Reset();
                bool closedAfterReset = !RuntimeAuditAccessGate.IsEnabled(runtimeAuditGateTestName);
                Check("RUNTIME-AUDIT-GATE-OPENS-ONLY-WHILE-EXPLICIT-EVENT-IS-SET",
                    runtimeAuditGateCreated && closedBeforeSet && openAfterSet && closedAfterReset,
                    ref pass, ref fail,
                    "A live audit endpoint is enabled only while the runner-owned manual-reset event exists and is signaled; reset closes the endpoint again.");
            }

            // 2.7.168: BrowserNode history anchors are accepted only after the required reads complete.
            var anchorBeforeEndMarker = new BrowserNodeFixture("FeatureBeforeEnd", false);
            var endMarkerNode = new BrowserNodeFixture("EndOfFeatures", true);
            var nodeAfterEndMarkerMustNotBeRead = new BrowserNodeFixture(() =>
                throw new InvalidOperationException("Nodes after the End-of-Features marker are outside the anchor boundary."));
            BrowserNodeFixture resolvedHistoryAnchor;
            string historyAnchorError;
            bool historyAnchorResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                new[] { anchorBeforeEndMarker, endMarkerNode, nodeAfterEndMarkerMustNotBeRead },
                node => node.ReadNativeObject(), native => native.IsEndMarker,
                out resolvedHistoryAnchor, out historyAnchorError);
            Check("BROWSER-TREE-ANCHOR-RESOLVES-LAST-NODE-BEFORE-END-MARKER",
                historyAnchorResolved && ReferenceEquals(resolvedHistoryAnchor, anchorBeforeEndMarker) &&
                string.IsNullOrEmpty(historyAnchorError), ref pass, ref fail,
                "The history anchor is the last readable top-level node before End-of-Features; nodes after the marker are not inspected.");

            var completeNoMarkerNodes = new[]
            {
                new BrowserNodeFixture("FeatureA", false),
                new BrowserNodeFixture("FeatureB", false)
            };
            BrowserNodeFixture lastNodeWithoutMarker;
            string noMarkerError;
            bool noMarkerResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                completeNoMarkerNodes, node => node.ReadNativeObject(), native => native.IsEndMarker,
                out lastNodeWithoutMarker, out noMarkerError);
            Check("BROWSER-TREE-ANCHOR-ACCEPTS-COMPLETE-INVENTORY-WITHOUT-VISIBLE-END-MARKER",
                noMarkerResolved && ReferenceEquals(lastNodeWithoutMarker, completeNoMarkerNodes[1]) &&
                string.IsNullOrEmpty(noMarkerError), ref pass, ref fail,
                "The established last-node fallback remains available only when the complete top-level inventory was read successfully.");

            var unreadableAnchorNodes = new[]
            {
                new BrowserNodeFixture("FeatureA", false),
                new BrowserNodeFixture(() => throw new InvalidOperationException("Synthetic NativeObject read failure.")),
                new BrowserNodeFixture("EndOfFeatures", true)
            };
            BrowserNodeFixture rejectedUnreadableAnchor;
            string unreadableAnchorError;
            bool unreadableAnchorResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                unreadableAnchorNodes, node => node.ReadNativeObject(), native => native.IsEndMarker,
                out rejectedUnreadableAnchor, out unreadableAnchorError);
            Check("BROWSER-TREE-ANCHOR-REJECTS-UNREADABLE-NATIVE-OBJECT",
                !unreadableAnchorResolved && rejectedUnreadableAnchor == null &&
                !string.IsNullOrWhiteSpace(unreadableAnchorError), ref pass, ref fail,
                "A NativeObject COM failure before End-of-Features cannot return a plausible but partial history anchor.");

            var nullNativeAnchorNodes = new[]
            {
                new BrowserNodeFixture("FeatureA", false),
                new BrowserNodeFixture((BrowserNativeFixture)null),
                new BrowserNodeFixture("EndOfFeatures", true)
            };
            BrowserNodeFixture rejectedNullNativeAnchor;
            string nullNativeAnchorError;
            bool nullNativeAnchorResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                nullNativeAnchorNodes, node => node.ReadNativeObject(), native => native.IsEndMarker,
                out rejectedNullNativeAnchor, out nullNativeAnchorError,
                node => node != null && node.IsBuiltInDefinition &&
                    !BrowserTreeAnchorPolicy.IsEndMarkerLabel(node.DefinitionLabel));
            Check("BROWSER-TREE-ANCHOR-REJECTS-NULL-NATIVE-OBJECT",
                !nullNativeAnchorResolved && rejectedNullNativeAnchor == null &&
                !string.IsNullOrWhiteSpace(nullNativeAnchorError), ref pass, ref fail,
                "A null NativeObject cannot be treated as proof that the node is not the End-of-Features marker.");

            var builtInStructuralNullNativeNode = new BrowserNodeFixture("Origin", true, null);
            var structuralNullNativeNodes = new[]
            {
                new BrowserNodeFixture("FeatureBeforeOrigin", false),
                builtInStructuralNullNativeNode,
                new BrowserNodeFixture("EndOfFeatures", true)
            };
            BrowserNodeFixture resolvedStructuralNullNativeAnchor;
            string structuralNullNativeError;
            bool structuralNullNativeResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                structuralNullNativeNodes, node => node.ReadNativeObject(), native => native.IsEndMarker,
                out resolvedStructuralNullNativeAnchor, out structuralNullNativeError,
                node => node != null && node.IsBuiltInDefinition &&
                    !BrowserTreeAnchorPolicy.IsEndMarkerLabel(node.DefinitionLabel));
            Check("BROWSER-TREE-ANCHOR-ACCEPTS-VERIFIED-BUILTIN-STRUCTURAL-NODE-WITHOUT-NATIVEOBJECT",
                structuralNullNativeResolved && ReferenceEquals(resolvedStructuralNullNativeAnchor, builtInStructuralNullNativeNode) &&
                string.IsNullOrEmpty(structuralNullNativeError), ref pass, ref fail,
                "A null NativeObject is accepted only when its BrowserNodeDefinition is readable, built-in, labelled and not an End-of-Part marker; it remains in the actual browser order.");

            var nullNativeEndMarkerNode = new BrowserNodeFixture("End of Part", true, null);
            BrowserNodeFixture rejectedNullNativeEndMarker;
            string nullNativeEndMarkerError;
            bool nullNativeEndMarkerResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                new[] { new BrowserNodeFixture("FeatureBeforeEnd", false), nullNativeEndMarkerNode },
                node => node.ReadNativeObject(), native => native.IsEndMarker,
                out rejectedNullNativeEndMarker, out nullNativeEndMarkerError,
                node => node != null && node.IsBuiltInDefinition &&
                    !BrowserTreeAnchorPolicy.IsEndMarkerLabel(node.DefinitionLabel));
            Check("BROWSER-TREE-ANCHOR-REJECTS-NULL-NATIVE-END-OF-PART-MARKER",
                !nullNativeEndMarkerResolved && rejectedNullNativeEndMarker == null &&
                !string.IsNullOrWhiteSpace(nullNativeEndMarkerError), ref pass, ref fail,
                "A built-in node whose readable label identifies End-of-Part is not accepted as an ordinary structural node when NativeObject is null.");

            BrowserNodeFixture rejectedNullBrowserNode;
            string nullBrowserNodeError;
            bool nullBrowserNodeResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                new BrowserNodeFixture[] { new BrowserNodeFixture("FeatureA", false), null },
                node => node.ReadNativeObject(), native => native.IsEndMarker,
                out rejectedNullBrowserNode, out nullBrowserNodeError);
            Check("BROWSER-TREE-ANCHOR-REJECTS-NULL-NODE-INVENTORY-ENTRY",
                !nullBrowserNodeResolved && rejectedNullBrowserNode == null &&
                !string.IsNullOrWhiteSpace(nullBrowserNodeError), ref pass, ref fail,
                "A null BrowserNode entry invalidates the anchor inventory rather than being silently skipped.");

            BrowserNodeFixture rejectedPartialAnchor;
            string partialAnchorError;
            bool partialAnchorResolved = BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(
                EnumerateBrowserNodesThenThrow(), node => node.ReadNativeObject(), native => native.IsEndMarker,
                out rejectedPartialAnchor, out partialAnchorError);
            Check("BROWSER-TREE-ANCHOR-REJECTS-PARTIAL-COLLECTION-ENUMERATION",
                !partialAnchorResolved && rejectedPartialAnchor == null &&
                !string.IsNullOrWhiteSpace(partialAnchorError), ref pass, ref fail,
                "A collection failure after a plausible candidate discards the candidate and prevents a partial inventory from becoming an anchor.");

            string threadBuilderBrowserSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int partModelPaneStart = threadBuilderBrowserSource.IndexOf("private static BrowserPane GetPartModelBrowser(", StringComparison.Ordinal);
            int partModelPaneEnd = partModelPaneStart < 0 ? -1 : threadBuilderBrowserSource.IndexOf(
                "internal static BrowserNode GetPackagingThreadBrowserAnchor(", partModelPaneStart, StringComparison.Ordinal);
            string partModelPaneMethod = partModelPaneStart >= 0 && partModelPaneEnd > partModelPaneStart
                ? threadBuilderBrowserSource.Substring(partModelPaneStart, partModelPaneEnd - partModelPaneStart)
                : string.Empty;
            Check("THREAD-BUILDER-ACTIVE-PANE-FAILURE-RETAINS-DIAGNOSED-COLLECTION-FALLBACK",
                partModelPaneMethod.Contains("ThreadBuilder.GetPartModelBrowser.ActivePaneFallback") &&
                partModelPaneMethod.Contains("ThreadBuilder.GetPartModelBrowser.PaneEnumeration") &&
                partModelPaneMethod.IndexOf("catch (Exception activePaneException)", StringComparison.Ordinal) >= 0 &&
                partModelPaneMethod.IndexOf("foreach (BrowserPane pane in panes)", StringComparison.Ordinal) >
                    partModelPaneMethod.IndexOf("catch (Exception activePaneException)", StringComparison.Ordinal) &&
                !partModelPaneMethod.Contains("catch { }"), ref pass, ref fail,
                "An unreadable ActivePane remains a diagnostic fallback condition; the BrowserPanes collection is still inspected, and collection failures are not silent.");

            int historyAnchorStart = threadBuilderBrowserSource.IndexOf("private static BrowserNode LastTopLevelNodeBeforeEop(", StringComparison.Ordinal);
            int historyAnchorEnd = historyAnchorStart < 0 ? -1 : threadBuilderBrowserSource.IndexOf(
                "private static IEnumerable<string> EnumerateBrowserFolderNames(", historyAnchorStart, StringComparison.Ordinal);
            string historyAnchorMethod = historyAnchorStart >= 0 && historyAnchorEnd > historyAnchorStart
                ? threadBuilderBrowserSource.Substring(historyAnchorStart, historyAnchorEnd - historyAnchorStart)
                : string.Empty;
            Check("THREAD-BUILDER-EOP-ANCHOR-REQUIRES-COMPLETE-TOPNODE-INVENTORY",
                historyAnchorMethod.Contains("BrowserTreeAnchorPolicy.TryFindLastBeforeMarker(") &&
                historyAnchorMethod.Contains("ThreadBuilder.LastTopLevelNodeBeforeEop.TopNode") &&
                historyAnchorMethod.Contains("ThreadBuilder.LastTopLevelNodeBeforeEop.InventoryIncomplete") &&
                historyAnchorMethod.Contains("throw new InvalidOperationException") &&
                !historyAnchorMethod.Contains("catch { }") && !historyAnchorMethod.Contains("return last;"),
                ref pass, ref fail,
                "An incomplete TopNode/BrowserNodes/NativeObject read aborts before feature creation instead of returning a partial EOP anchor.");

            string nativeConversionBrowserSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeThreadConversionService.cs"), Encoding.UTF8)
                : string.Empty;
            int conversionSnapshotStart = nativeConversionBrowserSource.IndexOf(
                "private static bool TryCaptureEndOfPartPosition(", StringComparison.Ordinal);
            int conversionSnapshotEnd = conversionSnapshotStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private static void RestoreEndOfPartPosition(", conversionSnapshotStart, StringComparison.Ordinal);
            string conversionSnapshotMethod = conversionSnapshotStart >= 0 && conversionSnapshotEnd > conversionSnapshotStart
                ? nativeConversionBrowserSource.Substring(conversionSnapshotStart, conversionSnapshotEnd - conversionSnapshotStart)
                : string.Empty;
            int containsObjectStart = nativeConversionBrowserSource.IndexOf("private static bool ContainsObject(", StringComparison.Ordinal);
            int containsObjectEnd = containsObjectStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private static bool ContainsHistoryObject(", containsObjectStart, StringComparison.Ordinal);
            string containsObjectMethod = containsObjectStart >= 0 && containsObjectEnd > containsObjectStart
                ? nativeConversionBrowserSource.Substring(containsObjectStart, containsObjectEnd - containsObjectStart)
                : string.Empty;
            int downstreamAnchorStart = nativeConversionBrowserSource.IndexOf(
                "private static object FindEndOfPartAnchorInNode(", StringComparison.Ordinal);
            int downstreamAnchorEnd = downstreamAnchorStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", downstreamAnchorStart, StringComparison.Ordinal);
            string downstreamAnchorMethod = downstreamAnchorStart >= 0 && downstreamAnchorEnd > downstreamAnchorStart
                ? nativeConversionBrowserSource.Substring(downstreamAnchorStart, downstreamAnchorEnd - downstreamAnchorStart)
                : string.Empty;
            Check("NATIVE-CONVERSION-EOP-SNAPSHOT-REJECTS-INCOMPLETE-NATIVEOBJECT-AND-ANCHOR-READS",
                conversionSnapshotMethod.Contains("if (!featureSearchComplete)") &&
                conversionSnapshotMethod.Contains("if (!anchorTraversalComplete)") &&
                conversionSnapshotMethod.Contains("ReadMarkerNativeObject.Index=") &&
                conversionSnapshotMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(nodes[i])") &&
                containsObjectMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)") &&
                containsObjectMethod.Contains("ComObjectIdentity.Compare(") &&
                containsObjectMethod.Contains("ComIdentityComparison.Unavailable") &&
                containsObjectMethod.Contains("out bool traversalComplete") &&
                downstreamAnchorMethod.Contains("out bool traversalComplete") &&
                downstreamAnchorMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)") &&
                downstreamAnchorMethod.Contains("traversalComplete = false;") &&
                conversionSnapshotMethod.Contains("AnchorSearchIncomplete.Index="),
                ref pass, ref fail,
                "Native thread conversion rejects incomplete feature-index or downstream-anchor evidence and can no longer misread a failed search as End-of-Part-at-bottom.");

            // 2.7.168: BrowserPane reads are captured once and history/selection resolution rejects partial COM evidence.
            string threadEditBrowserSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8)
                : string.Empty;
            string threadMetadataBrowserSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadMetadataStore.cs"), Encoding.UTF8)
                : string.Empty;

            int nativePaneStart = nativeConversionBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", StringComparison.Ordinal);
            int nativePaneEnd = nativePaneStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private static bool SameComObject(", nativePaneStart, StringComparison.Ordinal);
            string nativePaneMethod = nativePaneStart >= 0 && nativePaneEnd > nativePaneStart
                ? nativeConversionBrowserSource.Substring(nativePaneStart, nativePaneEnd - nativePaneStart)
                : string.Empty;
            Check("NATIVE-CONVERSION-BROWSER-PANE-LOOKUP-CAPTURES-ONE-COLLECTION-AND-DIAGNOSES-FALLBACK",
                nativePaneMethod.Contains("panes = doc.BrowserPanes") &&
                nativePaneMethod.Contains("panes.ActivePane") &&
                nativePaneMethod.Contains("foreach (BrowserPane pane in panes)") &&
                nativePaneMethod.Contains("NativeThreadConversionService.FindTreeBrowserPane.ActivePaneFallback") &&
                nativePaneMethod.Contains("NativeThreadConversionService.FindTreeBrowserPane.PaneEnumeration") &&
                nativePaneMethod.Split(new[] { "doc.BrowserPanes" }, StringSplitOptions.None).Length - 1 == 1,
                ref pass, ref fail,
                "The conversion path captures BrowserPanes once, logs ActivePane fallback, and enumerates the same current collection.");

            int editPaneStart = threadEditBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", StringComparison.Ordinal);
            int editPaneEnd = editPaneStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static object FindThreadMemberInNode(", editPaneStart, StringComparison.Ordinal);
            string editPaneMethod = editPaneStart >= 0 && editPaneEnd > editPaneStart
                ? threadEditBrowserSource.Substring(editPaneStart, editPaneEnd - editPaneStart)
                : string.Empty;
            Check("THREAD-EDIT-BROWSER-PANE-LOOKUP-CAPTURES-ONE-COLLECTION-AND-DIAGNOSES-FALLBACK",
                editPaneMethod.Contains("panes = doc.BrowserPanes") &&
                editPaneMethod.Contains("panes.ActivePane") &&
                editPaneMethod.Contains("foreach (BrowserPane pane in panes)") &&
                editPaneMethod.Contains("ThreadEditService.FindTreeBrowserPane.ActivePaneFallback") &&
                editPaneMethod.Contains("ThreadEditService.FindTreeBrowserPane.PaneEnumeration") &&
                editPaneMethod.Split(new[] { "doc.BrowserPanes" }, StringSplitOptions.None).Length - 1 == 1,
                ref pass, ref fail,
                "The edit path captures BrowserPanes once and keeps pane discovery fallback diagnostic rather than silent.");

            int metadataPaneStart = threadMetadataBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", StringComparison.Ordinal);
            int metadataPaneEnd = metadataPaneStart < 0 ? -1 : threadMetadataBrowserSource.IndexOf(
                "private static void CollectThreadIdsFromBrowserNode(", metadataPaneStart, StringComparison.Ordinal);
            string metadataPaneMethod = metadataPaneStart >= 0 && metadataPaneEnd > metadataPaneStart
                ? threadMetadataBrowserSource.Substring(metadataPaneStart, metadataPaneEnd - metadataPaneStart)
                : string.Empty;
            Check("METADATA-BROWSER-PANE-LOOKUP-CAPTURES-ONE-COLLECTION-AND-DIAGNOSES-FALLBACK",
                metadataPaneMethod.Contains("panes = doc.BrowserPanes") &&
                metadataPaneMethod.Contains("panes.ActivePane") &&
                metadataPaneMethod.Contains("foreach (BrowserPane pane in panes)") &&
                metadataPaneMethod.Contains("ThreadMetadataStore.FindTreeBrowserPane.ActivePaneFallback") &&
                metadataPaneMethod.Contains("ThreadMetadataStore.FindTreeBrowserPane.PaneEnumeration") &&
                metadataPaneMethod.Split(new[] { "doc.BrowserPanes" }, StringSplitOptions.None).Length - 1 == 1,
                ref pass, ref fail,
                "The metadata/selection path captures BrowserPanes once and preserves diagnosed collection fallback.");

            int editEopStart = threadEditBrowserSource.IndexOf(
                "private static bool TryCaptureEndOfPartPosition(", StringComparison.Ordinal);
            int editEopEnd = editEopStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static bool TryFindFirstThreadHistoryAnchor(", editEopStart, StringComparison.Ordinal);
            string editEopMethod = editEopStart >= 0 && editEopEnd > editEopStart
                ? threadEditBrowserSource.Substring(editEopStart, editEopEnd - editEopStart)
                : string.Empty;
            Check("THREAD-EDIT-EOP-SNAPSHOT-REJECTS-NULL-OR-UNREADABLE-TOPLEVEL-NODES",
                editEopMethod.Contains("rootNode = pane.TopNode") &&
                editEopMethod.Contains("var browserNodes = rootNode.BrowserNodes") &&
                editEopMethod.Contains("topLevelNodes = browserNodes.Cast<BrowserNode>().ToList()") &&
                editEopMethod.Contains("NullTopLevelNode.Index=") &&
                editEopMethod.Contains("TopNodeNativeObject.Index=") &&
                editEopMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(topNode)") &&
                editEopMethod.Contains("UnclassifiedNullNativeObject.Index=") &&
                editEopMethod.Contains("ThreadEditService.TryCaptureEndOfPartPosition.BrowserNodes"),
                ref pass, ref fail,
                "Edit history snapshots use the shared built-in structural-node classifier; unknown nulls and incomplete marker evidence still fail before WasAtBottom can be accepted.");

            int editAnchorStart = threadEditBrowserSource.IndexOf(
                "private static bool TryFindFirstThreadHistoryAnchor(", StringComparison.Ordinal);
            int editAnchorEnd = editAnchorStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", editAnchorStart, StringComparison.Ordinal);
            string editAnchorMethod = editAnchorStart >= 0 && editAnchorEnd > editAnchorStart
                ? threadEditBrowserSource.Substring(editAnchorStart, editAnchorEnd - editAnchorStart)
                : string.Empty;
            int threadMemberStart = threadEditBrowserSource.IndexOf(
                "private static object FindThreadMemberInNode(", StringComparison.Ordinal);
            int threadMemberEnd = threadMemberStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static object FindEndOfPartAnchorInNode(", threadMemberStart, StringComparison.Ordinal);
            string threadMemberMethod = threadMemberStart >= 0 && threadMemberEnd > threadMemberStart
                ? threadEditBrowserSource.Substring(threadMemberStart, threadMemberEnd - threadMemberStart)
                : string.Empty;

            int structuralClassifierStart = threadBuilderBrowserSource.IndexOf(
                "internal static bool IsKnownStructuralBrowserNodeWithoutNativeObject(", StringComparison.Ordinal);
            int structuralClassifierEnd = structuralClassifierStart < 0 ? -1 : threadBuilderBrowserSource.IndexOf(
                "private static IEnumerable<string> EnumerateBrowserFolderNames(", structuralClassifierStart, StringComparison.Ordinal);
            string structuralClassifierMethod = structuralClassifierStart >= 0 && structuralClassifierEnd > structuralClassifierStart
                ? threadBuilderBrowserSource.Substring(structuralClassifierStart, structuralClassifierEnd - structuralClassifierStart)
                : string.Empty;
            Check("BROWSER-TREE-NULL-NATIVE-CLASSIFIER-IS-SHARED-AND-FAILS-CLOSED",
                structuralClassifierMethod.Contains("node.BrowserNodeDefinition") &&
                structuralClassifierMethod.Contains("definition.BuiltIn") &&
                structuralClassifierMethod.Contains("definition.Label") &&
                structuralClassifierMethod.Contains("BrowserTreeAnchorPolicy.IsEndMarkerLabel(label)") &&
                structuralClassifierMethod.Contains("catch (Exception ex)") &&
                structuralClassifierMethod.Contains("return false;") &&
                editEopMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(topNode)") &&
                threadMemberMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)") &&
                containsObjectMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)") &&
                downstreamAnchorMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)"),
                ref pass, ref fail,
                "Build, standard edit and native-thread edit paths share one classifier: only a fully readable built-in non-EOP structural BrowserNode may have a null NativeObject; unknown/null/EOP nodes and definition-read failures remain fail-closed.");

            Check("THREAD-EDIT-ANCHOR-SEARCH-REJECTS-NULL-AND-INCOMPLETE-BROWSERNODE-TRAVERSAL",
                editAnchorMethod.Contains("NullTopLevelNode") &&
                editAnchorMethod.Contains("if (!traversalComplete)") &&
                threadMemberMethod.Contains("FindThreadMemberInNode.NullNode") &&
                threadMemberMethod.Contains("traversalComplete = false;") &&
                threadMemberMethod.Contains("FindThreadMemberInNode.NativeObject.Depth=") &&
                threadMemberMethod.Contains("ThreadBuilder.IsKnownStructuralBrowserNodeWithoutNativeObject(node)") &&
                threadMemberMethod.Contains("UnclassifiedNullNativeObject.Depth=") &&
                threadMemberMethod.Contains("FindThreadMemberInNode.BrowserNodes.Depth="),
                ref pass, ref fail,
                "Thread edit cannot turn a null node or a COM-failed subtree read into a valid insertion anchor.");

            int featureResolverStart = nativeConversionBrowserSource.IndexOf(
                "private static ThreadFeature ResolveFeatureFromBrowserNode(", StringComparison.Ordinal);
            int featureResolverEnd = featureResolverStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private sealed class StartEdgeAnalysis", featureResolverStart, StringComparison.Ordinal);
            string featureResolverMethod = featureResolverStart >= 0 && featureResolverEnd > featureResolverStart
                ? nativeConversionBrowserSource.Substring(featureResolverStart, featureResolverEnd - featureResolverStart)
                : string.Empty;
            Check("NATIVE-THREAD-RESOLUTION-REJECTS-INCOMPLETE-SELECTED-BROWSERNODE-SUBTREE",
                nativeConversionBrowserSource.Contains("ResolveFeatureFromBrowserNode(selectedNode, depth, out traversalComplete)") &&
                featureResolverMethod.Contains("NativeThreadConversionService.ResolveFeatureFromBrowserNode.NativeObject.Depth=") &&
                featureResolverMethod.Contains("NativeThreadConversionService.ResolveFeatureFromBrowserNode.BrowserNodes.Depth=") &&
                featureResolverMethod.Contains("NullChild.Depth=") &&
                featureResolverMethod.Contains("Ambiguous.Depth=") &&
                featureResolverMethod.Contains("traversalComplete = false;") &&
                featureResolverMethod.Contains("return descendantFeatures.Count == 1 ? descendantFeatures[0] : null;"),
                ref pass, ref fail,
                "A selected folder cannot resolve one child ThreadFeature when any node/children read is incomplete or the subtree is ambiguous.");

            int restoreAnchorStart = threadEditBrowserSource.IndexOf(
                "private static object FindFirstThreadHistoryAnchor(", StringComparison.Ordinal);
            int restoreAnchorEnd = restoreAnchorStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static ThreadFolderSnapshotOutcome FindThreadFolderNameForGroup(", restoreAnchorStart, StringComparison.Ordinal);
            string restoreAnchorMethod = restoreAnchorStart >= 0 && restoreAnchorEnd > restoreAnchorStart
                ? threadEditBrowserSource.Substring(restoreAnchorStart, restoreAnchorEnd - restoreAnchorStart)
                : string.Empty;
            Check("THREAD-EDIT-RESTORE-ANCHOR-CAPTURES-CURRENT-TOPNODE-AND-REJECTS-NULL-CHILDREN",
                restoreAnchorMethod.Contains("rootNode = pane.TopNode") &&
                restoreAnchorMethod.Contains("var browserNodes = rootNode.BrowserNodes") &&
                restoreAnchorMethod.Contains("topLevelNodes = browserNodes.Cast<BrowserNode>().ToList()") &&
                restoreAnchorMethod.Contains("if (node == null)") &&
                restoreAnchorMethod.Contains("if (!traversalComplete)"),
                ref pass, ref fail,
                "The post-mutation restore lookup uses one current root and aborts instead of skipping null or unreadable BrowserNode entries.");

            int selectedFeatureStart = nativeConversionBrowserSource.IndexOf(
                "internal static bool TryResolveSelectedFeature(", StringComparison.Ordinal);
            int selectedFeatureEnd = selectedFeatureStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private static int SafeSelectSetCount(", selectedFeatureStart, StringComparison.Ordinal);
            string selectedFeatureMethod = selectedFeatureStart >= 0 && selectedFeatureEnd > selectedFeatureStart
                ? nativeConversionBrowserSource.Substring(selectedFeatureStart, selectedFeatureEnd - selectedFeatureStart)
                : string.Empty;
            Check("NATIVE-THREAD-SELECTION-REQUIRES-COMPLETE-BROWSERNODE-SELECTION-INVENTORY",
                selectedFeatureMethod.Contains("TryGetSelectedBrowserNodes(doc, out selectedNodes, out browserError)") &&
                selectedFeatureMethod.Contains("BrowserSelectionIncomplete") &&
                selectedFeatureMethod.Contains("return false;") &&
                selectedFeatureMethod.IndexOf("BrowserSelectionIncomplete", StringComparison.Ordinal) <
                    selectedFeatureMethod.IndexOf("var matches = new List<ThreadFeature>()", StringComparison.Ordinal) &&
                !selectedFeatureMethod.Contains("BrowserFallback"),
                ref pass, ref fail,
                "A SelectSet candidate is not accepted as uniquely selected while the complementary BrowserNode selection inventory is unreadable.");

            int metadataSingleSelectionStart = threadMetadataBrowserSource.IndexOf(
                "internal static bool TryGetSingleSelectedBrowserNode(", StringComparison.Ordinal);
            int metadataSingleSelectionEnd = metadataSingleSelectionStart < 0 ? -1 : threadMetadataBrowserSource.IndexOf(
                "internal static bool TryGetSelectedBrowserNodes(", metadataSingleSelectionStart, StringComparison.Ordinal);
            string metadataSingleSelectionMethod = metadataSingleSelectionStart >= 0 && metadataSingleSelectionEnd > metadataSingleSelectionStart
                ? threadMetadataBrowserSource.Substring(metadataSingleSelectionStart, metadataSingleSelectionEnd - metadataSingleSelectionStart)
                : string.Empty;
            int metadataMultiSelectionStart = metadataSingleSelectionEnd;
            int metadataMultiSelectionEnd = metadataMultiSelectionStart < 0 ? -1 : threadMetadataBrowserSource.IndexOf(
                "internal static bool TryResolveSelectedThreadMetadata(", metadataMultiSelectionStart, StringComparison.Ordinal);
            string metadataMultiSelectionMethod = metadataMultiSelectionStart >= 0 && metadataMultiSelectionEnd > metadataMultiSelectionStart
                ? threadMetadataBrowserSource.Substring(metadataMultiSelectionStart, metadataMultiSelectionEnd - metadataMultiSelectionStart)
                : string.Empty;
            Check("THREAD-METADATA-SELECTION-SCANS-ONE-CAPTURED-CURRENT-TOPNODE",
                metadataSingleSelectionMethod.Contains("rootNode = pane.TopNode") &&
                metadataSingleSelectionMethod.Contains("CollectSelectedBrowserNodes(rootNode, selected") &&
                metadataSingleSelectionMethod.Split(new[] { "pane.TopNode" }, StringSplitOptions.None).Length - 1 == 1 &&
                metadataMultiSelectionMethod.Contains("rootNode = pane.TopNode") &&
                metadataMultiSelectionMethod.Contains("CollectSelectedBrowserNodes(rootNode, selectedNodes") &&
                metadataMultiSelectionMethod.Split(new[] { "pane.TopNode" }, StringSplitOptions.None).Length - 1 == 1 &&
                !metadataSingleSelectionMethod.Contains("CollectSelectedBrowserNodes(pane.TopNode") &&
                !metadataMultiSelectionMethod.Contains("CollectSelectedBrowserNodes(pane.TopNode"),
                ref pass, ref fail,
                "Each selected-node inventory is rooted in one captured current TopNode; no helper silently re-reads the COM property during traversal.");

            int createdBrowserNodesStart = threadBuilderBrowserSource.IndexOf(
                "private static ObjectCollection CollectCreatedTopLevelBrowserNodes(", StringComparison.Ordinal);
            int createdBrowserNodesEnd = createdBrowserNodesStart < 0 ? -1 : threadBuilderBrowserSource.IndexOf(
                "internal static bool SameBrowserComObject(", createdBrowserNodesStart, StringComparison.Ordinal);
            string createdBrowserNodesMethod = createdBrowserNodesStart >= 0 && createdBrowserNodesEnd > createdBrowserNodesStart
                ? threadBuilderBrowserSource.Substring(createdBrowserNodesStart, createdBrowserNodesEnd - createdBrowserNodesStart)
                : string.Empty;
            int createdTopNodeRead = createdBrowserNodesMethod.IndexOf("topNode = pane.TopNode", StringComparison.Ordinal);
            int createdTopNodeNullGuard = createdBrowserNodesMethod.IndexOf("if (topNode == null)", createdTopNodeRead, StringComparison.Ordinal);
            int createdObjectFallback = createdBrowserNodesMethod.IndexOf("pane.GetBrowserNodeFromObject(obj)", StringComparison.Ordinal);
            Check("THREAD-BUILDER-CREATED-NODE-FALLBACK-REQUIRES-CURRENT-ROOT-FOR-PARENT-PROOF",
                createdTopNodeRead >= 0 && createdTopNodeNullGuard > createdTopNodeRead &&
                createdBrowserNodesMethod.IndexOf("throw new InvalidOperationException", createdTopNodeNullGuard, StringComparison.Ordinal) > createdTopNodeNullGuard &&
                createdObjectFallback > createdTopNodeNullGuard &&
                createdBrowserNodesMethod.Contains("node.Parent") &&
                createdBrowserNodesMethod.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.ParentLookup"),
                ref pass, ref fail,
                "Per-object BrowserNode fallback is retained only after one current TopNode is captured, and unreadable parent membership is rejected.");

            int candidateResolverStart = nativeConversionBrowserSource.IndexOf(
                "private static bool TryResolveFeatureCandidateForSelection(", StringComparison.Ordinal);
            int candidateResolverEnd = candidateResolverStart < 0 ? -1 : nativeConversionBrowserSource.IndexOf(
                "private sealed class StartEdgeAnalysis", candidateResolverStart, StringComparison.Ordinal);
            string candidateResolverMethod = candidateResolverStart >= 0 && candidateResolverEnd > candidateResolverStart
                ? nativeConversionBrowserSource.Substring(candidateResolverStart, candidateResolverEnd - candidateResolverStart)
                : string.Empty;
            Check("NATIVE-THREAD-SELECTION-CANDIDATES-REQUIRE-COMPLETE-BROWSERNODE-RESOLUTION",
                candidateResolverMethod.Contains("ResolveFeatureFromBrowserNode(node, 0, out resolutionComplete)") &&
                candidateResolverMethod.Contains("ResolveFeatureFromBrowserNode(folderNode, 1, out resolutionComplete)") &&
                candidateResolverMethod.Contains("resolutionComplete = false;") &&
                selectedFeatureMethod.Contains("TryResolveFeatureCandidateForSelection(candidate, out resolved, out candidateResolutionComplete)") &&
                selectedFeatureMethod.Contains("!candidateResolutionComplete") &&
                selectedFeatureMethod.Contains("CandidateResolutionIncomplete"),
                ref pass, ref fail,
                "A selected BrowserNode/BrowserFolder candidate that cannot be fully inspected cannot be ignored while a different candidate is accepted as unique.");

            // 2.6.162: live SurfaceBody ownership must be proved from a complete current inventory.
            var bodyInventory = new[] { new BodyNameFixture("Solid1"), new BodyNameFixture("Solid2") };
            BodyNameFixture resolvedBody;
            string bodyResolutionError;
            bool uniqueBodyResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                bodyInventory, body => body.Name, "solid1", out resolvedBody, out bodyResolutionError);
            Check("SURFACE-BODY-RESOLUTION-REQUIRES-ONE-CASE-INSENSITIVE-EXACT-LIVE-MATCH",
                uniqueBodyResolved && ReferenceEquals(resolvedBody, bodyInventory[0]) && string.IsNullOrEmpty(bodyResolutionError),
                ref pass, ref fail,
                "The current SurfaceBodies inventory resolves one exact body name case-insensitively without relying on a saved COM reference.");

            BodyNameFixture missingBody;
            string missingBodyError;
            bool missingBodyResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                bodyInventory, body => body.Name, "Solid3", out missingBody, out missingBodyError);
            Check("SURFACE-BODY-RESOLUTION-REJECTS-MISSING-LIVE-BODY",
                !missingBodyResolved && missingBody == null && !string.IsNullOrWhiteSpace(missingBodyError),
                ref pass, ref fail,
                "A body absent from the current document inventory cannot be replaced by a stale pre-operation COM reference.");

            BodyNameFixture duplicateBody;
            string duplicateBodyError;
            bool duplicateBodyResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                new[] { new BodyNameFixture("Solid1"), new BodyNameFixture("solid1") },
                body => body.Name, "Solid1", out duplicateBody, out duplicateBodyError);
            Check("SURFACE-BODY-RESOLUTION-REJECTS-DUPLICATE-EXACT-NAMES",
                !duplicateBodyResolved && duplicateBody == null && !string.IsNullOrWhiteSpace(duplicateBodyError),
                ref pass, ref fail,
                "Two current bodies with the same logical name are ambiguous and cannot be arbitrarily selected.");

            BodyNameFixture unreadableBody;
            string unreadableBodyError;
            bool unreadableBodyResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                EnumerateBodyNamesThenThrow(), body => body.Name, "Solid1",
                out unreadableBody, out unreadableBodyError);
            Check("SURFACE-BODY-RESOLUTION-REJECTS-PARTIAL-ENUMERATION-AFTER-MATCH",
                !unreadableBodyResolved && unreadableBody == null && !string.IsNullOrWhiteSpace(unreadableBodyError),
                ref pass, ref fail,
                "Finding a matching body before a later enumeration exception is not proof of unique ownership; the partial result is discarded.");

            BodyNameFixture unreadableNameBody;
            string unreadableNameError;
            bool unreadableNameResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                new[] { new BodyNameFixture("Solid1"), new BodyNameFixture("Solid2") },
                body => body.Name == "Solid2" ? throw new InvalidOperationException("Synthetic COM Name read failure.") : body.Name,
                "Solid1", out unreadableNameBody, out unreadableNameError);
            Check("SURFACE-BODY-RESOLUTION-REJECTS-UNREADABLE-NONMATCHING-BODY-NAME",
                !unreadableNameResolved && unreadableNameBody == null && !string.IsNullOrWhiteSpace(unreadableNameError),
                ref pass, ref fail,
                "A failure reading any body name makes the full inventory incomplete even when an earlier entry matched.");

            BodyNameFixture nullEntryBody;
            string nullEntryError;
            bool nullEntryResolved = SurfaceBodyResolutionPolicy.TryResolveUnique(
                new BodyNameFixture[] { new BodyNameFixture("Solid1"), null }, body => body.Name,
                "Solid1", out nullEntryBody, out nullEntryError);
            Check("SURFACE-BODY-RESOLUTION-REJECTS-NULL-INVENTORY-ENTRY",
                !nullEntryResolved && nullEntryBody == null && !string.IsNullOrWhiteSpace(nullEntryError),
                ref pass, ref fail,
                "A null element in SurfaceBodies invalidates the inventory rather than allowing a false unique match.");

            string threadBuilderBodyOwnershipSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int ownedBodyStart = threadBuilderBodyOwnershipSource.IndexOf("private static SurfaceBody OwnedBody(", StringComparison.Ordinal);
            int ownedBodyEnd = ownedBodyStart < 0 ? -1 : threadBuilderBodyOwnershipSource.IndexOf("// Circular edges of coaxial cylinders", ownedBodyStart, StringComparison.Ordinal);
            string ownedBodyMethod = ownedBodyStart >= 0 && ownedBodyEnd > ownedBodyStart
                ? threadBuilderBodyOwnershipSource.Substring(ownedBodyStart, ownedBodyEnd - ownedBodyStart) : string.Empty;
            Check("THREAD-BUILDER-OWNED-BODY-FAILS-CLOSED-WITHOUT-STALE-RCW-FALLBACK",
                ownedBodyMethod.Contains("SurfaceBodyResolutionPolicy.TryResolveUnique(") &&
                ownedBodyMethod.Contains("EnumerateSurfaceBodies(def)") &&
                ownedBodyMethod.Contains("ThreadBuilder.OwnedBody.ResolveFailed") &&
                !ownedBodyMethod.Contains("return t.Body;") && !ownedBodyMethod.Contains("catch { }") ,
                ref pass, ref fail,
                "Affected-body resolution must enumerate the complete current SurfaceBodies collection, require one exact body-name match, log failures, and never fall back to the pre-build ThreadTarget.Body RCW.");

            string edgeSelectorBodyOwnershipSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "EdgeSelector.cs"), Encoding.UTF8)
                : string.Empty;
            int restoreEdgeStart = edgeSelectorBodyOwnershipSource.IndexOf("private Edge FindMatchingEdge(", StringComparison.Ordinal);
            // FindMatchingEdge is followed by the live-inventory iterator. The old EdgeCenter
            // boundary was removed when stale-geometry helpers were replaced by the strict reader.
            int restoreEdgeEnd = restoreEdgeStart < 0 ? -1 : edgeSelectorBodyOwnershipSource.IndexOf("private static IEnumerable<SurfaceBody> EnumerateSurfaceBodies(", restoreEdgeStart, StringComparison.Ordinal);
            string restoreEdgeMethod = restoreEdgeStart >= 0 && restoreEdgeEnd > restoreEdgeStart
                ? edgeSelectorBodyOwnershipSource.Substring(restoreEdgeStart, restoreEdgeEnd - restoreEdgeStart) : string.Empty;
            Check("EDGE-SELECTOR-UNDO-RESTORE-USES-CURRENT-UNIQUE-BODY-NOT-SNAPSHOT-RCW",
                restoreEdgeMethod.Contains("EnumerateSurfaceBodies(pd)") &&
                restoreEdgeMethod.Contains("SurfaceBodyResolutionPolicy.TryResolveUnique(") &&
                restoreEdgeMethod.Contains("EdgeSelector.FindMatchingEdge.CurrentBodyScan") &&
                restoreEdgeMethod.Contains("TryReadCircularEdgeIdentity(edge, out center, out radius, out isCircular, out geometryReadError)") &&
                restoreEdgeMethod.Contains("matchingEdgeCount++;") && restoreEdgeMethod.Contains("if (matchingEdgeCount > 1)") &&
                restoreEdgeMethod.Contains("return matchingEdgeCount == 1 ? matchingEdge : null;") &&
                !restoreEdgeMethod.Contains("EdgeCenter(edge)") && !restoreEdgeMethod.Contains("EdgeRadius(edge)") &&
                !Regex.IsMatch(restoreEdgeMethod, @"\bsnapshot\s*\.\s*Body\b") &&
                !restoreEdgeMethod.Contains("IEnumerable<SurfaceBody> bodies"),
                ref pass, ref fail,
                "Undo selection restoration must resolve the body only from a complete current document inventory and must not scan the saved, potentially stale snapshot.Body RCW.");

            int edgeGeometryReaderStart = edgeSelectorBodyOwnershipSource.IndexOf("private static bool TryReadCircularEdgeIdentity(", StringComparison.Ordinal);
            int edgeGeometryReaderEnd = edgeGeometryReaderStart < 0 ? -1 : edgeSelectorBodyOwnershipSource.IndexOf("private static double Distance3(", edgeGeometryReaderStart, StringComparison.Ordinal);
            string edgeGeometryReaderMethod = edgeGeometryReaderStart >= 0 && edgeGeometryReaderEnd > edgeGeometryReaderStart
                ? edgeSelectorBodyOwnershipSource.Substring(edgeGeometryReaderStart, edgeGeometryReaderEnd - edgeGeometryReaderStart) : string.Empty;
            Check("EDGE-SELECTOR-RESTORE-DISTINGUISHES-NONCIRCULAR-EDGES-FROM-COM-READ-FAILURES",
                edgeGeometryReaderMethod.Contains("CurveTypeEnum geometryType = edge.GeometryType;") &&
                edgeGeometryReaderMethod.Contains("if (geometryType != CurveTypeEnum.kCircleCurve && geometryType != CurveTypeEnum.kCircularArcCurve)") &&
                edgeGeometryReaderMethod.Contains("catch (Exception ex)") &&
                edgeGeometryReaderMethod.Contains("failure = ex;") &&
                !edgeGeometryReaderMethod.Contains("catch { }") &&
                edgeGeometryReaderMethod.Contains("double.IsNaN(radius)"),
                ref pass, ref fail,
                "A normal non-circular edge is skipped, but a COM failure reading a circular edge's geometry/center/radius invalidates Undo restoration instead of becoming null/zero.");

            int analyzeBodyStart = threadBuilderBodyOwnershipSource.IndexOf("public static ThreadTarget Analyze(Inventor.Application app, Edge edge)", StringComparison.Ordinal);
            int analyzeBodyEnd = analyzeBodyStart < 0 ? -1 : threadBuilderBodyOwnershipSource.IndexOf("private static void AttachNativeTaperedMetadata", analyzeBodyStart, StringComparison.Ordinal);
            string analyzeBodyMethod = analyzeBodyStart >= 0 && analyzeBodyEnd > analyzeBodyStart
                ? threadBuilderBodyOwnershipSource.Substring(analyzeBodyStart, analyzeBodyEnd - analyzeBodyStart) : string.Empty;
            Check("THREAD-BUILDER-ANALYZE-REQUIRES-NONEMPTY-LIVE-SURFACE-BODY-IDENTITY",
                analyzeBodyMethod.Contains("selectedFace.SurfaceBody") &&
                analyzeBodyMethod.Contains("string.IsNullOrWhiteSpace(ownerName)") &&
                analyzeBodyMethod.Contains("ThreadBuilder.Analyze.SurfaceBody") &&
                analyzeBodyMethod.Contains("throw new InvalidOperationException(") &&
                !analyzeBodyMethod.Contains("try { ownerBody = selectedFace.SurfaceBody; ownerName = ownerBody.Name; } catch { }"),
                ref pass, ref fail,
                "Edge analysis must fail before geometry creation if the selected face's owning SurfaceBody or its name cannot be read.");

            string simulatedFolderName = "CapNeck old parameters";
            int simulatedSetterCalls = 0;
            bool renameMutationAttempted;
            string renamedFolderName;
            string renameError;
            bool renameSucceeded = BrowserFolderRenamePolicy.TryApply("CapNeck new parameters",
                () => simulatedFolderName,
                value =>
                {
                    simulatedSetterCalls++;
                    simulatedFolderName = value;
                    if (simulatedSetterCalls == 1)
                        throw new InvalidOperationException("Synthetic COM setter failure after applying the value.");
                },
                out renameMutationAttempted, out renamedFolderName, out renameError);
            Check("BROWSER-FOLDER-RENAME-POST-SET-FAILURE-RESTORES-ORIGINAL-AND-REPORTS-MUTATION",
                !renameSucceeded && renameMutationAttempted && simulatedSetterCalls == 2 &&
                simulatedFolderName == "CapNeck old parameters" &&
                renamedFolderName == "CapNeck old parameters" &&
                !string.IsNullOrWhiteSpace(renameError) && renameError.Contains("исходное имя восстановлено"),
                ref pass, ref fail,
                "A simulated COM setter that changes BrowserFolder.Name and then throws must restore/read back the old name while retaining the mutation-attempted signal so the caller aborts the enclosing transaction.");

            string successfulFolderName = "CapNeck old parameters";
            bool successfulRenameMutationAttempted;
            string successfulFinalName;
            string successfulRenameError;
            bool successfulRename = BrowserFolderRenamePolicy.TryApply("CapNeck new parameters",
                () => successfulFolderName, value => successfulFolderName = value,
                out successfulRenameMutationAttempted, out successfulFinalName, out successfulRenameError);
            Check("BROWSER-FOLDER-RENAME-SUCCESS-REQUIRES-READBACK",
                successfulRename && successfulRenameMutationAttempted &&
                successfulFolderName == "CapNeck new parameters" && successfulFinalName == successfulFolderName &&
                string.IsNullOrEmpty(successfulRenameError),
                ref pass, ref fail,
                "A normal folder rename is successful only after the requested value is read back from the same property accessor.");

            int preflightSetterCalls = 0;
            bool preflightMutationAttempted;
            string preflightFinalName;
            string preflightError;
            bool preflightRename = BrowserFolderRenamePolicy.TryApply("CapNeck new parameters",
                () => throw new InvalidOperationException("Synthetic preflight read failure."),
                value => preflightSetterCalls++,
                out preflightMutationAttempted, out preflightFinalName, out preflightError);
            Check("BROWSER-FOLDER-RENAME-PREFLIGHT-FAILURE-DOES-NOT-REPORT-MUTATION",
                !preflightRename && !preflightMutationAttempted && preflightSetterCalls == 0 &&
                !string.IsNullOrWhiteSpace(preflightError),
                ref pass, ref fail,
                "A failure before invoking the COM setter remains eligible for the existing best-effort no-mutation path.");

            string uniqueFolderName;
            string uniqueFolderError;
            bool uniqueFolderResolved = ThreadFolderUniqueNamePolicy.TryResolveUniqueName("Thread — ISO · M10",
                new[] { "Thread — ISO · M10", "Thread — ISO · M10 #2", "Thread — ISO · M10 #4" },
                out uniqueFolderName, out uniqueFolderError);
            Check("BROWSER-FOLDER-UNIQUE-NAME-USES-HIGHEST-EXISTING-SUFFIX",
                uniqueFolderResolved && uniqueFolderName == "Thread — ISO · M10 #5" && string.IsNullOrEmpty(uniqueFolderError),
                ref pass, ref fail,
                "A complete folder inventory must preserve the existing monotonic suffix rule and choose the next suffix after the highest occupied numeric name.");

            string baseFolderName;
            string baseFolderError;
            bool baseFolderResolved = ThreadFolderUniqueNamePolicy.TryResolveUniqueName("Thread — ISO · M12",
                new[] { "Thread — ISO · M12 #2" }, out baseFolderName, out baseFolderError);
            Check("BROWSER-FOLDER-UNIQUE-NAME-USES-BASE-WHEN-IT-IS-FREE",
                baseFolderResolved && baseFolderName == "Thread — ISO · M12" && string.IsNullOrEmpty(baseFolderError),
                ref pass, ref fail,
                "An existing numbered folder does not reserve the unsuffixed base label; the original naming behavior is retained when the exact base label is free.");

            int exactFolderIndex;
            string exactFolderError;
            bool exactFolderFound = ThreadFolderUniqueNamePolicy.TryFindUniqueExactName(
                new[] { "Thread — ISO · M10 #2", "Thread — ISO · M10" }, "Thread — ISO · M10",
                out exactFolderIndex, out exactFolderError);
            Check("BROWSER-FOLDER-LOOKUP-REQUIRES-ONE-EXACT-NAME",
                exactFolderFound && exactFolderIndex == 1 && string.IsNullOrEmpty(exactFolderError),
                ref pass, ref fail,
                "Fallback lookup must resolve only a unique exact BrowserFolder name, not a substring or a numbered sibling.");

            int duplicateFolderIndex;
            string duplicateFolderError;
            bool duplicateFolderFound = ThreadFolderUniqueNamePolicy.TryFindUniqueExactName(
                new[] { "Thread — ISO · M10", "Thread — ISO · M10", "Thread — ISO · M10 #2" },
                "Thread — ISO · M10", out duplicateFolderIndex, out duplicateFolderError);
            Check("BROWSER-FOLDER-LOOKUP-REJECTS-DUPLICATE-EXACT-NAMES",
                !duplicateFolderFound && duplicateFolderIndex == -1 && !string.IsNullOrWhiteSpace(duplicateFolderError),
                ref pass, ref fail,
                "A duplicate exact name is ambiguous and must block fallback grouping instead of selecting an arbitrary folder.");

            int failedLookupIndex;
            string failedLookupError;
            bool failedLookupCompleted = ThreadFolderUniqueNamePolicy.TryFindUniqueExactName(
                EnumerateFolderNamesThenThrow(), "Thread — ISO · M10", out failedLookupIndex, out failedLookupError);
            bool failedUniqueNameCompleted = ThreadFolderUniqueNamePolicy.TryResolveUniqueName("Thread — ISO · M10",
                EnumerateFolderNamesThenThrow(), out uniqueFolderName, out uniqueFolderError);
            Check("BROWSER-FOLDER-INVENTORY-ENUMERATION-FAILURE-FAILS-CLOSED",
                !failedLookupCompleted && failedLookupIndex == -1 && !string.IsNullOrWhiteSpace(failedLookupError) &&
                !failedUniqueNameCompleted && uniqueFolderName == null && !string.IsNullOrWhiteSpace(uniqueFolderError),
                ref pass, ref fail,
                "An exception partway through BrowserFolders enumeration cannot be converted into 'not found' or the unsuffixed base name.");

            string threadBuilderFolderSafetySource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadBuilder.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            Check("BROWSER-FOLDER-COM-ENUMERATION-FAILURE-ABORTS-CREATION-AND-FALLBACK",
                threadBuilderFolderSafetySource.Contains("TryGetUniqueThreadFolderName(topNode, baseName, out name, out uniqueNameError)") &&
                threadBuilderFolderSafetySource.Contains("abortOnPartialGrouping = true;") &&
                threadBuilderFolderSafetySource.Contains("TryFindBrowserFolderByName(topNode, name, out folder, out folderLookupError)") &&
                threadBuilderFolderSafetySource.Contains("fallback-browser-folder-lookup-incomplete") &&
                !threadBuilderFolderSafetySource.Contains("private static string UniqueThreadFolderName(") &&
                !threadBuilderFolderSafetySource.Contains("private static BrowserFolder FindBrowserFolderByName("),
                ref pass, ref fail,
                "Production BrowserFolder naming and fallback lookup must fail closed when the COM folder inventory is incomplete; they must not create a competing folder.");
            int optionalFolderInventorySkipStart = threadBuilderFolderSafetySource.IndexOf("if (bestEffortExistingFolderRepairFailure)", StringComparison.Ordinal);
            int requiredFolderInventoryAbort = threadBuilderFolderSafetySource.IndexOf("abortOnPartialGrouping = true;", optionalFolderInventorySkipStart < 0 ? 0 : optionalFolderInventorySkipStart);
            Check("BROWSER-FOLDER-INVENTORY-FAILURE-PRESERVES-OPTIONAL-CAPNECK-EDIT-PATH-BEFORE-MUTATION",
                optionalFolderInventorySkipStart >= 0 && requiredFolderInventoryAbort > optionalFolderInventorySkipStart &&
                threadBuilderFolderSafetySource.Contains("no-folder-mutation-attempted=true") &&
                threadBuilderFolderSafetySource.Contains("metadataAttachment=required; geometryCommit=allowed"),
                ref pass, ref fail,
                "An existing Cap/Neck edit may skip optional BrowserFolder regrouping only when inventory fails before any folder mutation; new builds and post-mutation failures remain transaction-fatal.");

            Check("BROWSER-FOLDER-REPAIR-TREE-LIMITS-AND-COM-BRANCH-FAILURES-ARE-DIAGNOSED-AND-FAIL-CLOSED",
                threadBuilderFolderSafetySource.Contains("FolderRepairMaxTreeDepth = 16") &&
                threadBuilderFolderSafetySource.Contains("FolderRepairMaxTreeNodes = 5000") &&
                threadBuilderFolderSafetySource.Contains("folder-repair-tree-depth-limit") &&
                threadBuilderFolderSafetySource.Contains("folder-repair-tree-node-limit") &&
                threadBuilderFolderSafetySource.Contains("folder-repair-tree-enumeration-failed") &&
                threadBuilderFolderSafetySource.Contains("if (truncated)") &&
                threadBuilderFolderSafetySource.Contains("repairMustAbortOnFailure = true;"),
                ref pass, ref fail,
                "The bounded BrowserTree scan retains its 16-level/5000-node safety limits, records the exact truncation cause and rejects the repair instead of inferring ownership from a partial tree.");

            string edgeStatusRu = HelivantaLocalization.GetForLanguage("EdgeSelector.StatusBar.SelectCircularStartEdges", "ru");
            string edgeStatusEn = HelivantaLocalization.GetForLanguage("EdgeSelector.StatusBar.SelectCircularStartEdges", "en");
            Check("LOCALIZATION-EDGE-STATUS-BAR-HAS-RU-EN-RESOURCE-PAIR",
                HelivantaLocalization.ContainsCyrillic(edgeStatusRu) && !HelivantaLocalization.ContainsCyrillic(edgeStatusEn),
                ref pass, ref fail, "The Inventor selection status-bar prompt must be localized in both UI languages.");
            Check("LOCALIZATION-DISPLAY-CULTURE-IS-INDEPENDENT-OF-WINDOWS-REGION",
                string.Format(HelivantaLocalization.GetDisplayCultureForLanguage("ru"), "{0:0.###}", 12.5) == "12,5" &&
                string.Format(HelivantaLocalization.GetDisplayCultureForLanguage("en"), "{0:0.###}", 12.5) == "12.5",
                ref pass, ref fail, "Display numbers must use comma in RU and dot in EN independently of the host regional culture.");

            string packagingSummaryStart = "";
            if (!string.IsNullOrWhiteSpace(sourceRoot) && File.Exists(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs")))
            {
                string source = File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs"), Encoding.UTF8);
                int start = source.IndexOf("string kindDescription =", StringComparison.Ordinal);
                int end = source.IndexOf("diagnosticStage = \"commit-transaction\"", start < 0 ? 0 : start, StringComparison.Ordinal);
                packagingSummaryStart = start >= 0 && end > start ? source.Substring(start, end - start) : string.Empty;
            }
            string packagingDialogSourceForLocalization = !string.IsNullOrWhiteSpace(sourceRoot) && File.Exists(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8) : string.Empty;
            Check("CAP-NECK-USER-SUMMARY-USES-DISPLAY-CULTURE-AND-SEMANTIC-FOLDER-RESOURCE",
                packagingSummaryStart.Contains("HelivantaLocalization.DisplayCulture") &&
                packagingSummaryStart.Contains("PackagingThreadGeometryBuilder.Summary.FolderGrouped") &&
                !packagingSummaryStart.Contains("CultureInfo.InvariantCulture") &&
                packagingDialogSourceForLocalization.Contains("FormatCompletedBuildSummaryForLanguage") &&
                packagingDialogSourceForLocalization.Contains("VentCenterAnglesDegrees") &&
                packagingDialogSourceForLocalization.Contains("GetStartPhaseDegrees(i, summary.Starts)"),
                ref pass, ref fail, "All user-facing Cap/Neck summary numbers, phase/vent angles and folder labels must be re-rendered from structured values in the selected RU/EN language.");

            string packagingThreadBuilderSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            string edgeSelectorSource = !string.IsNullOrWhiteSpace(sourceRoot) && File.Exists(Path.Combine(sourceRoot, "EdgeSelector.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "EdgeSelector.cs"), Encoding.UTF8) : string.Empty;
            string threadDialogSource = !string.IsNullOrWhiteSpace(sourceRoot) && File.Exists(Path.Combine(sourceRoot, "ThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8) : string.Empty;
            Check("LOCALIZATION-EDGE-SELECTOR-STATUS-BAR-USES-SEMANTIC-RESOURCE",
                edgeSelectorSource.Contains("HelivantaLocalization.Get(\"EdgeSelector.StatusBar.SelectCircularStartEdges\")") &&
                !edgeSelectorSource.Contains("StatusBarText = \"Helivanta: выберите"),
                ref pass, ref fail, "Inventor status-bar text must be resource-backed rather than a raw Russian literal.");
            Check("LOCALIZATION-CONFIRMATION-ACCESSIBILITY-USES-SEMANTIC-RESOURCES",
                threadDialogSource.Contains("ThreadDialog.Confirmation.NoAccessibleDescription") &&
                threadDialogSource.Contains("ThreadDialog.Confirmation.ExitAccessibleDescription") &&
                threadDialogSource.Contains("BindResourceAccessibleDescription(yesButton"),
                ref pass, ref fail, "Confirmation dialog accessibility names/descriptions must be bound to RU/EN resources.");
            int createCurrentThreadParamsStart = threadDialogSource.IndexOf(
                "private ThreadParams CreateCurrentThreadParams()", StringComparison.Ordinal);
            int applyTaperModeUiStart = threadDialogSource.IndexOf(
                "private void ApplyTaperModeUI(", createCurrentThreadParamsStart < 0 ? 0 : createCurrentThreadParamsStart,
                StringComparison.Ordinal);
            string createCurrentThreadParamsSource = createCurrentThreadParamsStart >= 0 && applyTaperModeUiStart > createCurrentThreadParamsStart
                ? threadDialogSource.Substring(createCurrentThreadParamsStart,
                    applyTaperModeUiStart - createCurrentThreadParamsStart) : string.Empty;
            Check("THREADDIALOG-CYLINDRICAL-PARAMETERS-CLEAR-HIDDEN-TAPER-STATE",
                createCurrentThreadParamsSource.Contains("tapered && cbTaperStandard != null") &&
                createCurrentThreadParamsSource.Contains("tapered && cbTaperDesignation != null") &&
                createCurrentThreadParamsSource.Contains("TaperRatio = tapered && nTaperRatio != null") &&
                createCurrentThreadParamsSource.Contains("TaperReferenceDiameterMm = tapered ? dia : 0.0") &&
                createCurrentThreadParamsSource.Contains("TaperReferenceLengthMm = tapered && nLen != null") &&
                createCurrentThreadParamsSource.Contains("TaperStartDiameterMm = tapered ? dia : 0.0") &&
                createCurrentThreadParamsSource.Contains("TaperEndDiameterMm = tapered && nTaperRatio != null && nLen != null"),
                ref pass, ref fail,
                "A hidden tapered-standard selection must not leak into a new cylindrical ThreadParams snapshot after mode switching.");

            int packagingTransactionEndPosition = packagingThreadBuilderSource.IndexOf("transaction.End();", StringComparison.Ordinal);
            int packagingPreparedSummaryPosition = packagingThreadBuilderSource.IndexOf("string preparedSummary =", StringComparison.Ordinal);
            int packagingPreparedDetailsPosition = packagingThreadBuilderSource.IndexOf("PackagingThreadBuildSummary preparedSummaryDetails", StringComparison.Ordinal);
            int packagingCatchPosition = packagingThreadBuilderSource.IndexOf("catch (Exception ex)", packagingTransactionEndPosition < 0 ? 0 : packagingTransactionEndPosition, StringComparison.Ordinal);
            Check("CAP-NECK-RESULT-PREPARATION-COMPLETES-BEFORE-TRANSACTION-COMMIT",
                packagingPreparedSummaryPosition >= 0 && packagingPreparedDetailsPosition >= 0 &&
                packagingTransactionEndPosition > packagingPreparedSummaryPosition &&
                packagingTransactionEndPosition > packagingPreparedDetailsPosition && packagingCatchPosition > packagingTransactionEndPosition,
                ref pass, ref fail, "All localized success text and summary allocation must complete before Inventor commits the Cap/Neck transaction.");
            Check("CAP-NECK-TRANSACTION-LIFECYCLE-HAS-READ-ONLY-STAGE-DIAGNOSTICS",
                packagingThreadBuilderSource.Contains("StartTransaction.before") &&
                packagingThreadBuilderSource.Contains("StartTransaction.after") &&
                packagingThreadBuilderSource.Contains("Transaction.End.before") &&
                packagingThreadBuilderSource.Contains("Transaction.End.after") &&
                packagingThreadBuilderSource.Contains("Transaction.Abort.before") &&
                packagingThreadBuilderSource.Contains("Transaction.Abort.after") &&
                packagingThreadBuilderSource.Contains("Transaction.Abort.failed") &&
                packagingThreadBuilderSource.Contains("bool abortSucceeded = !transactionStarted || transactionCommitted;"),
                ref pass, ref fail, "Transaction lifecycle diagnostics must observe start, commit and abort boundaries without changing transaction order.");

            string threadBuilderSourceForTransactions = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadBuilder.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int buildAllStart = threadBuilderSourceForTransactions.IndexOf("public static int BuildAll(", StringComparison.Ordinal);
            int buildAllEnd = threadBuilderSourceForTransactions.IndexOf("// ---------- thread creation ----------", buildAllStart < 0 ? 0 : buildAllStart, StringComparison.Ordinal);
            string buildAllSource = buildAllStart >= 0 && buildAllEnd > buildAllStart
                ? threadBuilderSourceForTransactions.Substring(buildAllStart, buildAllEnd - buildAllStart)
                : string.Empty;
            int buildAllEndCall = buildAllSource.IndexOf("tr.End();", StringComparison.Ordinal);
            int committedFlag = buildAllSource.IndexOf("transactionCommitted = true;", StringComparison.Ordinal);
            int abortCall = buildAllSource.IndexOf("TryAbortBuildTransactionWithRetries(tr", StringComparison.Ordinal);
            Check("BUILD-ALL-REQUIRES-REAL-TRANSACTION-BEFORE-GEOMETRY",
                buildAllSource.Contains("if (tr == null)") &&
                buildAllSource.IndexOf("if (tr == null)", StringComparison.Ordinal) < buildAllSource.IndexOf("BuildAfterWallThicknessValidation", StringComparison.Ordinal),
                ref pass, ref fail, "BuildAll must stop before creating geometry if Inventor does not return a transaction object.");
            Check("BUILD-ALL-NEVER-ABORTS-AFTER-COMMIT",
                buildAllEndCall >= 0 && committedFlag > buildAllEndCall && abortCall > committedFlag &&
                buildAllSource.Contains("if (transactionCommitted)") &&
                buildAllSource.IndexOf("if (transactionCommitted)", abortCall, StringComparison.Ordinal) < 0,
                ref pass, ref fail, "Once Transaction.End succeeds, post-commit reporting failures must not enter the Abort/recovery path.");
            Check("BUILD-ALL-ABORT-USES-BOUNDED-THREE-ATTEMPT-RETRY",
                threadBuilderSourceForTransactions.Contains("TryAbortBuildTransactionWithRetries(tr, app, buildDocument") &&
                threadBuilderSourceForTransactions.Contains("for (int attempt = 1; attempt <= 3; attempt++)") &&
                threadBuilderSourceForTransactions.Contains("new AggregateException(") &&
                threadBuilderSourceForTransactions.Contains("Transaction.Abort.retry.failed") &&
                threadBuilderSourceForTransactions.Contains("AbortAttemptsAggregate") &&
                threadBuilderSourceForTransactions.Contains("if (failures.Count > 0)"),
                ref pass, ref fail, "A transient COM Abort failure receives at most three diagnosed attempts; every failed attempt is retained in an AggregateException even when a later retry succeeds.");
            string threadBuildRollbackPolicySource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadBuildRollbackPolicy.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuildRollbackPolicy.cs"), Encoding.UTF8)
                : string.Empty;
            Check("THREAD-BUILD-ROLLBACK-SNAPSHOT-INCLUDES-COMPLETE-THREAD-METADATA",
                threadBuilderSourceForTransactions.Contains("TryLoadAllGroups(doc, out groups, out metadataError)") &&
                threadBuilderSourceForTransactions.Contains("SHA256.HashData") &&
                threadBuilderSourceForTransactions.Contains("JsonSerializer.Serialize(group.Record)") &&
                threadBuilderSourceForTransactions.Contains("group.Record.ThreadId") &&
                threadBuilderSourceForTransactions.Contains("OrderBy(item =>") &&
                threadBuilderSourceForTransactions.Contains("StringComparer.Ordinal") &&
                threadBuilderSourceForTransactions.Contains("group.MembersByIndex == null") &&
                threadBuilderSourceForTransactions.Contains("group.MembersByIndex.Count") &&
                threadBuilderSourceForTransactions.Contains("Convert.ToHexString(digest)") &&
                threadBuildRollbackPolicySource.Contains("original.MetadataGroupCount != restored.MetadataGroupCount") &&
                threadBuildRollbackPolicySource.Contains("original.MetadataMemberCount != restored.MetadataMemberCount") &&
                threadBuildRollbackPolicySource.Contains("original.MetadataFingerprint, restored.MetadataFingerprint, StringComparison.Ordinal"),
                ref pass, ref fail, "Pre/post rollback snapshots must enumerate all valid groups, create an ordinally ordered SHA-256 fingerprint from each record and member count, and compare group count, member count and fingerprint.");

            string threadEditServiceSourceForPackaging = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadEditService.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8)
                : string.Empty;
            string threadDialogSourceForPersistedOverlap = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            string threadStandardLocalizationPolicySourceForPersistedOverlap = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadStandardLocalizationPolicy.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadStandardLocalizationPolicy.cs"), Encoding.UTF8)
                : string.Empty;
            Check("PERSISTED-OVERLAP-POLICY-USED-BY-CREATE-AND-EDIT",
                threadDialogSourceForPersistedOverlap.Contains("ThreadMetadataOverlapPolicy.TryValidateNoOverlap") &&
                threadEditServiceSourceForPackaging.Contains("ThreadMetadataOverlapPolicy.TryValidateNoOverlap") &&
                threadDialogSourceForPersistedOverlap.Contains("ThreadMetadataStore.TryLoadAllGroups(activePart") &&
                threadDialogSourceForPersistedOverlap.Contains("persisted-thread-overlap-rejected"),
                ref pass, ref fail, "Создание и редактирование используют одну политику, загружая полный сохранённый инвентарь перед геометрической транзакцией.");
            Check("REDRAWWINDOW-PINVOKE-USES-REAL-USER32-ENTRYPOINT",
                threadDialogSourceForPersistedOverlap.Contains("EntryPoint = \"RedrawWindow\"") &&
                threadDialogSourceForPersistedOverlap.Contains("RedrawWindowNative(Handle"),
                ref pass, ref fail, "P/Invoke явно привязан к экспортируемому user32 RedrawWindow, сохраняя managed-имя вызова.");
            string packagingThreadDialogSourceForFolderLanguage = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            Check("CAPNECK-NEW-BROWSER-FOLDER-SNAPSHOTS-CURRENT-UI-LANGUAGE",
                packagingThreadDialogSourceForFolderLanguage.Contains("string newBuildFolderNameLanguage = ThreadBrowserFolderPolicy.NormalizeLanguage(") &&
                packagingThreadDialogSourceForFolderLanguage.Contains("null, newBuildFolderNameLanguage, out candidateSummary") &&
                packagingThreadDialogSourceForFolderLanguage.Contains("null, newBuildFolderNameLanguage, out successfulBuildSummary") &&
                packagingThreadBuilderSource.Contains("folderNameLanguage ?? HelivantaUserSettings.Current.Language"),
                ref pass, ref fail,
                "Every new Cap/Neck build snapshots the current UI language once and passes it explicitly through both the normal and auto-pitch build paths.");

            int packagingFolderCall = packagingThreadBuilderSource.IndexOf("BrowserNode packagingFolderNode = ThreadBuilder.CreatePackagingThreadBrowserFolder", StringComparison.Ordinal);
            int packagingMetadataAttach = packagingThreadBuilderSource.IndexOf("ThreadMetadataStore.TryAttachPackaging", packagingFolderCall < 0 ? 0 : packagingFolderCall, StringComparison.Ordinal);
            int capNeckEditStart = threadEditServiceSourceForPackaging.IndexOf("internal static bool TryReplacePackaging(", StringComparison.Ordinal);
            int capNeckStandardEditStart = threadEditServiceSourceForPackaging.IndexOf("internal static bool TryReplace(Inventor.Application", capNeckEditStart < 0 ? 0 : capNeckEditStart, StringComparison.Ordinal);
            string capNeckEditSource = capNeckEditStart >= 0 && capNeckStandardEditStart > capNeckEditStart
                ? threadEditServiceSourceForPackaging.Substring(capNeckEditStart, capNeckStandardEditStart - capNeckEditStart)
                : string.Empty;
            int capNeckFolderLanguageCapture = capNeckEditSource.IndexOf("packagingFolderSnapshotOutcome = FindThreadFolderNameForGroup", StringComparison.Ordinal);
            int capNeckFirstDelete = capNeckEditSource.IndexOf("definition.DeleteObjects(single, true, true, true)", StringComparison.Ordinal);
            Check("CAPNECK-EDIT-CAPTURES-MODEL-FOLDER-LANGUAGE-BEFORE-REPLACEMENT",
                capNeckFolderLanguageCapture >= 0 && capNeckFirstDelete > capNeckFolderLanguageCapture &&
                capNeckEditSource.Contains("FindThreadFolderNameForGroup(doc, threadId,") &&
                capNeckEditSource.Contains("true, out originalPackagingFolderName") &&
                capNeckEditSource.Contains("ResolvePackagingThreadFolderNameLanguage") &&
                capNeckEditSource.Contains("packagingFolderNameLanguage, out summaryDetails") &&
                packagingThreadBuilderSource.Contains("effectiveFolderNameLanguage") &&
                packagingThreadBuilderSource.Contains("p.Starts, rightHand, internalThread, effectiveFolderNameLanguage)"),
                ref pass, ref fail,
                "Cap/Neck edit must snapshot the unique language-bearing folder before the first feature deletion and use the same resolved language for BrowserFolder creation and its success summary.");
            Check("CAPNECK-REPLACEMENT-BROWSER-FOLDER-MAPPING-IS-BEST-EFFORT",
                packagingThreadBuilderSource.Contains("bool isEditReplacement = !string.IsNullOrWhiteSpace(threadIdOverride)") &&
                packagingThreadBuilderSource.Contains("bool requireFolderMapping = !isEditReplacement") &&
                threadBuilderSourceForTransactions.Contains("bestEffortExistingFolderRepairFailure: !requireFolderMapping") &&
                threadBuilderSourceForTransactions.Contains("bool requireFolderMapping = true") &&
                threadBuilderSourceForTransactions.Contains("if (!requireFolderMapping)") &&
                threadBuilderSourceForTransactions.Contains("packaging-thread-browser-folder-skipped"),
                ref pass, ref fail,
                "A Cap/Neck replacement may commit when Inventor cannot expose helper BrowserNodes; new Cap/Neck builds still require successful folder mapping and partial grouping failures remain fatal.");
            int repairFolderStart = threadBuilderSourceForTransactions.IndexOf("private static bool TryRepairSharedExistingThreadFolder", StringComparison.Ordinal);
            int repairFolderEnd = threadBuilderSourceForTransactions.IndexOf("private static bool TryRenameExistingThreadFolder", repairFolderStart < 0 ? 0 : repairFolderStart, StringComparison.Ordinal);
            string repairFolderSource = repairFolderStart >= 0 && repairFolderEnd > repairFolderStart
                ? threadBuilderSourceForTransactions.Substring(repairFolderStart, repairFolderEnd - repairFolderStart)
                : string.Empty;
            int namedFolderStart = threadBuilderSourceForTransactions.IndexOf("private static BrowserNode CreateNamedBrowserFolder", StringComparison.Ordinal);
            int namedFolderEnd = threadBuilderSourceForTransactions.IndexOf("private static BrowserNode CreateNativeTaperedThreadBrowserFolder", namedFolderStart < 0 ? 0 : namedFolderStart, StringComparison.Ordinal);
            string namedFolderSource = namedFolderStart >= 0 && namedFolderEnd > namedFolderStart
                ? threadBuilderSourceForTransactions.Substring(namedFolderStart, namedFolderEnd - namedFolderStart)
                : string.Empty;
            int repairMutationFlagPosition = repairFolderSource.IndexOf("repairMutationAttempted = true;", StringComparison.Ordinal);
            int repairMutationCallPosition = repairFolderSource.IndexOf("candidate.Add(node);", StringComparison.Ordinal);
            int partialFallbackCountPosition = namedFolderSource.IndexOf("if (addedCount != resolvedCount)", StringComparison.Ordinal);
            int partialFallbackFlagPosition = partialFallbackCountPosition >= 0
                ? namedFolderSource.IndexOf("abortOnPartialGrouping = true;", partialFallbackCountPosition, StringComparison.Ordinal)
                : -1;
            Check("CAPNECK-REPLACEMENT-PARTIAL-BROWSER-GROUPING-FAILS-CLOSED",
                namedFolderSource.Contains("if (bestEffortExistingFolderRepairFailure && !repairMutationAttempted)") &&
                repairMutationFlagPosition >= 0 && repairMutationCallPosition > repairMutationFlagPosition &&
                partialFallbackCountPosition >= 0 && partialFallbackFlagPosition > partialFallbackCountPosition &&
                namedFolderSource.Contains("HelivantaErrorCode.FallbackFolderGroupingIncomplete") &&
                namedFolderSource.Contains("folderSafetyError.Code == HelivantaErrorCode.TaperedFolderRepairIncomplete") &&
                namedFolderSource.Contains("if (mustAbortForFolderSafety) throw;"),
                ref pass, ref fail,
                "Best-effort replacement may skip folder repair only before any Folder.Add mutation; once grouping starts, incomplete fallback or existing-folder repair must propagate and abort the enclosing transaction.");
            Check("CAPNECK-REPLACEMENT-METADATA-ATTACHMENT-REMAINS-MANDATORY",
                packagingFolderCall >= 0 && packagingMetadataAttach > packagingFolderCall &&
                packagingThreadBuilderSource.Contains("if (!ThreadMetadataStore.TryAttachPackaging(allCreatedObjects") &&
                packagingThreadBuilderSource.Contains("HelivantaErrorCode.ThreadMetadataAttachmentFailed") &&
                packagingThreadBuilderSource.Contains("BrowserFolderName = packagingFolderNode != null ? browserFolderName : null"),
                ref pass, ref fail,
                "Optional browser-folder creation must not weaken mandatory metadata attachment to every replacement feature or advertise a folder that was not created.");
            Check("CAPNECK-REPLACEMENT-PRESERVES-OPTIONAL-FOLDER-WARNING",
                threadEditServiceSourceForPackaging.Contains("ThreadBuilder.LastWarning = null;") &&
                threadEditServiceSourceForPackaging.Contains("warning = ThreadBuilder.LastWarning;") &&
                threadEditServiceSourceForPackaging.Contains("transaction.End();"),
                ref pass, ref fail,
                "A successful Cap/Neck replacement commits and surfaces the localized browser-folder warning instead of discarding it or rolling back valid geometry.");

            // Regression coverage for the installed add-in layout: the installer embeds and
            // deploys Helivanta.dll only, so both catalogs must be available from that assembly
            // without an en\Helivanta.resources.dll satellite.
            string[] resourceNames = typeof(Program).Assembly.GetManifestResourceNames();
            string russianCatalogTitle = null;
            string englishCatalogTitle = null;
            string englishCatalogThreadPrefix = null;
            ResourceManager russianCatalog = null;
            ResourceManager englishCatalog = null;
            try
            {
                russianCatalog = new ResourceManager("ThreadPro.Resources.HelivantaStrings", typeof(Program).Assembly);
                englishCatalog = new ResourceManager("ThreadPro.Resources.HelivantaStrings.en", typeof(Program).Assembly);
                russianCatalogTitle = russianCatalog.GetString("Settings.WindowTitle", CultureInfo.InvariantCulture);
                englishCatalogTitle = englishCatalog.GetString("Settings.WindowTitle", CultureInfo.InvariantCulture);
                englishCatalogThreadPrefix = englishCatalog.GetString("BrowserFolder.ThreadPrefix", CultureInfo.InvariantCulture);
            }
            catch (MissingManifestResourceException) { }
            catch (InvalidOperationException) { }
            Check("LOCALIZATION-RU-EN-CATALOGS-EMBEDDED-IN-PRIMARY-ASSEMBLY",
                resourceNames.Contains("ThreadPro.Resources.HelivantaStrings.resources", StringComparer.Ordinal) &&
                resourceNames.Contains("ThreadPro.Resources.HelivantaStrings.en.resources", StringComparer.Ordinal),
                ref pass, ref fail, "The installed add-in assembly must carry both RU and EN catalogs as separate manifest resources.");
            Check("LOCALIZATION-EN-CATALOG-DOES-NOT-FALL-BACK-TO-RUSSIAN",
                russianCatalogTitle == "Настройки Helivanta" && englishCatalogTitle == "Helivanta Settings" &&
                englishCatalogThreadPrefix == "Thread — ",
                ref pass, ref fail, "The English catalog must resolve English values inside the primary assembly, without relying on a satellite assembly.");

            string engineeringDiagramSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadEngineeringDiagram.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadEngineeringDiagram.cs"), Encoding.UTF8)
                : string.Empty;
            string[] engineeringDiagramKeys = Regex.Matches(engineeringDiagramSource, "\"(ThreadEngineeringDiagram\\.[^\"]+)\"")
                .Cast<Match>().Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            bool engineeringDiagramResourcesComplete = engineeringDiagramKeys.Length == 76 &&
                engineeringDiagramKeys.Contains("ThreadEngineeringDiagram.Standard.Gost6211TaperTitle", StringComparer.Ordinal) &&
                russianCatalog != null && englishCatalog != null &&
                engineeringDiagramKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            bool engineeringDiagramEnglishOnly = engineeringDiagramResourcesComplete &&
                engineeringDiagramKeys.All(key => !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty)
                    .Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            bool engineeringDiagramPlaceholderParity = engineeringDiagramResourcesComplete &&
                engineeringDiagramKeys.All(key =>
                    string.Join("|", Regex.Matches(russianCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})")
                        .Cast<Match>().Select(match => match.Groups[1].Value).OrderBy(value => value, StringComparer.Ordinal)) ==
                    string.Join("|", Regex.Matches(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})")
                        .Cast<Match>().Select(match => match.Groups[1].Value).OrderBy(value => value, StringComparer.Ordinal)));
            Check("THREAD-ENGINEERING-DIAGRAM-ALL-RESOURCE-PLACEHOLDERS-MATCH-RU-EN",
                engineeringDiagramPlaceholderParity,
                ref pass, ref fail, "All engineering diagram RU/EN resource pairs must expose identical format placeholder indexes.");
            Check("THREAD-ENGINEERING-DIAGRAM-USES-76-SEMANTIC-RU-EN-KEYS",
                engineeringDiagramResourcesComplete && engineeringDiagramEnglishOnly &&
                !engineeringDiagramSource.Contains("HelivantaLocalization.Translate(") &&
                engineeringDiagramSource.Contains("g.DrawString(metric.Key") &&
                engineeringDiagramSource.Contains("g.DrawString(metric.Value"),
                ref pass, ref fail, "All engineering diagram labels, metrics, notes and accessibility text must resolve from complete RU/EN semantic resources; external size designations remain untouched.");
            bool engineeringDiagramFormatTemplatesValid = russianCatalog != null && englishCatalog != null &&
                (russianCatalog.GetString("ThreadEngineeringDiagram.AccessibleName.Selection", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{0}") &&
                (russianCatalog.GetString("ThreadEngineeringDiagram.AccessibleName.Selection", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{2}") &&
                (englishCatalog.GetString("ThreadEngineeringDiagram.AccessibleName.Selection", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{0}") &&
                (englishCatalog.GetString("ThreadEngineeringDiagram.Metric.ProfileHeight", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{0}") &&
                (russianCatalog.GetString("ThreadEngineeringDiagram.TaperNote.Resolved", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{3}") &&
                (englishCatalog.GetString("ThreadEngineeringDiagram.TaperNote.Resolved", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{3}");
            Check("THREAD-ENGINEERING-DIAGRAM-RU-EN-FORMAT-TEMPLATES-PRESERVE-ARGUMENTS",
                engineeringDiagramFormatTemplatesValid,
                ref pass, ref fail, "Accessible names, profile-height labels and resolved taper notes must retain matching RU/EN format placeholders.");
            Check("THREAD-ENGINEERING-DIAGRAM-ACCESSIBILITY-REFRESHES-AFTER-LANGUAGE-SWITCH",
                engineeringDiagramSource.Contains("_localizedLanguage") &&
                engineeringDiagramSource.Contains("HelivantaUserSettings.Current.Language") &&
                engineeringDiagramSource.Contains("string.Equals(_localizedLanguage, language, StringComparison.OrdinalIgnoreCase)") &&
                Regex.Matches(engineeringDiagramSource, "AccessibleDescription = T\\(\"ThreadEngineeringDiagram.AccessibleDescription\"\\)").Count >= 2,
                ref pass, ref fail, "An unchanged diagram state must still refresh its accessible name when the selected UI language changes.");

            string[] comStringValues;
            string comStringIssue;
            Check("COM-STRING-SEQUENCE-ACCEPTS-STRING-ARRAY",
                ComStringSequenceReader.TryRead(new[] { "NPT", "JIS Taper" }, out comStringValues, out comStringIssue) &&
                comStringValues.Length == 2 && comStringValues[0] == "NPT" && comStringIssue == null,
                ref pass, ref fail, "A normal string[] returned through COM must be preserved without conversion.");
            Check("COM-STRING-SEQUENCE-ACCEPTS-OBJECT-ARRAY-OF-STRINGS",
                ComStringSequenceReader.TryRead(new object[] { "NPT", null, "JIS Taper" }, out comStringValues, out comStringIssue) &&
                comStringValues.Length == 3 && comStringValues[0] == "NPT" && comStringValues[1] == null && comStringValues[2] == "JIS Taper",
                ref pass, ref fail, "String-compatible COM object arrays may be normalized while preserving null elements for caller filtering.");
            Check("COM-STRING-SEQUENCE-REJECTS-MIXED-OBJECT-ARRAY",
                !ComStringSequenceReader.TryRead(new object[] { "NPT", 42 }, out comStringValues, out comStringIssue) &&
                comStringValues.Length == 0 && comStringIssue != null && comStringIssue.Contains("index 1"),
                ref pass, ref fail, "A mixed COM array must not silently turn into a partial set of thread-table entries.");
            Check("COM-STRING-SEQUENCE-REJECTS-NULL-RESULT",
                !ComStringSequenceReader.TryRead(null, out comStringValues, out comStringIssue) &&
                comStringValues.Length == 0 && comStringIssue != null && comStringIssue.Contains("null"),
                ref pass, ref fail, "A null COM result must be distinguished from a valid empty sequence in diagnostics.");
            Check("COM-STRING-SEQUENCE-REJECTS-SCALAR-STRING",
                !ComStringSequenceReader.TryRead("NPT", out comStringValues, out comStringIssue) &&
                comStringValues.Length == 0 && comStringIssue != null && comStringIssue.Contains("scalar string"),
                ref pass, ref fail, "A scalar string must not be treated as an enumerable list of characters.");

            string[] stableUiErrorKeys = {
                "ThreadBuilder.BuildFailure.Generic", "ThreadBuilder.BuildFailure.Suggestions", "ThreadBuilder.BuildFailure.SafeStateWarning",
                "ThreadBuilder.Error.RollbackUnverified", "ThreadBuilder.Error.RollbackSnapshotUnavailable",
                "ThreadBuilder.Chamfer.CreateFailed",
                "ThreadDialog.Preview.TransparencyFallbackWarning", "ThreadDialog.Preview.ShadedMeshFallbackReason",
                "ThreadDialog.Preview.GraphicsCreationFailed", "TaperedThread.BuildFailure.Generic",
                "NativeTaperedThreadBuilder.Error.PhysicalProfileCreationFailed", "NativeTaperedThreadBuilder.Error.PhysicalCoilCreationFailed",
                "NativeTaperedThreadBuilder.Error.PhysicalGrooveCreationFailed", "NativeTaperedThreadBuilder.Error.RunoutSegmentCreationFailed",
                "NativeTaperedThreadBuilder.Warning.RunoutFallback",
                "ThreadMetadataStore.Error.SerializeFailed", "ThreadMetadataStore.Error.AttachFailed",
                "ThreadMetadataStore.Error.ReadGroupFailed", "ThreadMetadataStore.Error.EnumerateGroupsFailed",
                "ThreadMetadataStore.Error.ReadSelectionFailed", "ThreadMetadataStore.Error.EmptyMetadataSet",
                "ThreadMetadataStore.Error.OwnerScanFailed",
                "ThreadEditService.Error.ReplacementFailed.BeforeTransaction",
                "ThreadEditService.Error.ReplacementFailed.RollbackVerified",
                "ThreadEditService.Error.ReplacementFailed.RollbackUnverified",
                "ThreadEditService.Error.EndOfPartSnapshotFailed", "ThreadEditService.Error.HistoryAnchorFailed",
                "ThreadEditService.Error.BrowserFolderLookupFailed", "ThreadEditService.Error.BrowserFolderRenameFailed",
                "ThreadEditService.Error.HistoryVerificationFailed", "ThreadEditService.Error.FeatureOrderVerificationFailed",
                "ThreadEditService.Error.OriginalCylinderRestoreFailed", "ThreadEditService.Error.OriginalEdgeAnalysisFailed",
                "ThreadWallThickness.Error.MeasurementFailed", "ThreadWallThickness.Error.TaperedMeasurementFailed",
                "ThreadWallThickness.Error.PreflightRejected",
                "ThreadWallThickness.Error.CylindricalWallTooThin", "ThreadWallThickness.Error.TaperedWallTooThin",
                "ThreadWallThickness.Preflight.MissingInputs", "ThreadWallThickness.Preflight.NoPartDocument",
                "ThreadWallThickness.Preflight.DifferentDocument", "ThreadWallThickness.Preflight.BodyUnavailable",
                "ThreadWallThickness.Preflight.BodyNotSolid", "ThreadWallThickness.Preflight.ThreadSideMismatch",
                "ThreadWallThickness.Preflight.CylinderGeometryIncomplete", "ThreadWallThickness.Preflight.InvalidParameters",
                "ThreadWallThickness.Preflight.LengthOffsetOutOfRange", "ThreadWallThickness.Preflight.ProfileDepthUnavailable",
                "ThreadWallThickness.Preflight.EffectiveDepthInvalid", "ThreadWallThickness.Preflight.RayMeasurementFailed",
                "ThreadWallThickness.Preflight.MeasuredDistanceInvalid", "ThreadWallThickness.Preflight.MeasurementIncomplete",
                "ThreadWallThickness.Preflight.Tapered.MissingInputs", "ThreadWallThickness.Preflight.Tapered.NoPartDocument",
                "ThreadWallThickness.Preflight.Tapered.DifferentDocument", "ThreadWallThickness.Preflight.Tapered.BodyNotSolid",
                "ThreadWallThickness.Preflight.Tapered.ThreadSideMismatch", "ThreadWallThickness.Preflight.Tapered.InvalidGeometryParameters",
                "ThreadWallThickness.Preflight.Tapered.EffectiveDepthUnavailable", "ThreadWallThickness.Preflight.Tapered.RequiredWallUnavailable",
                "ThreadWallThickness.Preflight.Tapered.LocalRadiusInvalid", "ThreadWallThickness.Preflight.Tapered.RayOffsetInvalid",
                "ThreadWallThickness.Preflight.Tapered.RayMeasurementFailed", "ThreadWallThickness.Preflight.Tapered.AmbiguousRayBoundary",
                "ThreadWallThickness.Preflight.Tapered.OuterWallPointMissing", "ThreadWallThickness.Preflight.Tapered.MeasurementInvalid",
                "ThreadWallThickness.Preflight.Tapered.MeasurementIncomplete",
                "PackagingThreadGeometryBuilder.Error.RunoutSpanTooShort",
                "PackagingThreadGeometryBuilder.Error.InternalWallMeasurementFailed",
                "PackagingThreadGeometryBuilder.Error.ExternalWallMeasurementFailed",
                "PackagingThreadGeometryBuilder.Error.CoreProfileCreationFailed",
                "PackagingThreadGeometryBuilder.Error.CoreCoilCreationFailed",
                "PackagingThreadGeometryBuilder.Error.CoreCoilVerificationFailed"
            };
            bool stableUiErrorsHaveBothLanguages = russianCatalog != null && englishCatalog != null &&
                stableUiErrorKeys.All(key => !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                                             !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            bool stableUiErrorsEnglishHasCyrillic = englishCatalog == null || stableUiErrorKeys.Any(key =>
                (englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("UI-FAILURE-SUMMARIES-HAVE-RU-EN-RESOURCES", stableUiErrorsHaveBothLanguages,
                ref pass, ref fail, "User-facing failure summaries and preview fallbacks must have semantic keys in both catalogs.");
            Check("UI-FAILURE-EN-RESOURCES-CONTAIN-NO-CYRILLIC", !stableUiErrorsEnglishHasCyrillic,
                ref pass, ref fail, "English UI error resources must not fall back to Russian text.");
            string chamferCreateFailedRu = russianCatalog == null ? null : russianCatalog.GetString("ThreadBuilder.Chamfer.CreateFailed", CultureInfo.InvariantCulture);
            string chamferCreateFailedEn = englishCatalog == null ? null : englishCatalog.GetString("ThreadBuilder.Chamfer.CreateFailed", CultureInfo.InvariantCulture);
            Check("CHAMFER-COM-FAILURE-RESOURCE-KEEPS-COMPLETE-RU-EN-RECOVERY-GUIDANCE",
                !string.IsNullOrWhiteSpace(chamferCreateFailedRu) && chamferCreateFailedRu.Contains("Что попробовать:") &&
                chamferCreateFailedRu.Contains("Снимите флажок «Фаски»") && chamferCreateFailedRu.Contains("\n") &&
                !string.IsNullOrWhiteSpace(chamferCreateFailedEn) && chamferCreateFailedEn.Contains("What to try:") &&
                chamferCreateFailedEn.Contains("Clear the Chamfers option") && chamferCreateFailedEn.Contains("\n") &&
                !chamferCreateFailedEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "The chamfer COM failure should retain the full actionable guidance in both catalogs with clean English and multiline formatting.");
            Check("THREAD-WALL-MEASUREMENT-AND-PREFLIGHT-ERROR-CODES-ARE-STABLE",
                (int)HelivantaErrorCode.WallThicknessMeasurementFailed == 12 &&
                (int)HelivantaErrorCode.TaperedWallThicknessMeasurementFailed == 13 &&
                (int)HelivantaErrorCode.WallThicknessPreflightRejected == 29,
                ref pass, ref fail, "New wall-measurement failures must use append-only, language-neutral error-code values.");
            Check("PACKAGING-CORE-STAGE-ERROR-CODES-ARE-APPEND-ONLY",
                (int)HelivantaErrorCode.PackagingCoreProfileCreationFailed == 30 &&
                (int)HelivantaErrorCode.PackagingCoreCoilCreationFailed == 31 &&
                (int)HelivantaErrorCode.PackagingCoreCoilVerificationFailed == 32 &&
                (int)HelivantaErrorCode.PackagingThreadRunoutSpanTooShort == 33,
                ref pass, ref fail, "Core-profile, core-Coil, core-Coil verification and insufficient cap/neck runout span use append-only error codes.");
            Check("PACKAGING-CORE-STAGE-FAILURES-ARE-TYPED-AND-DIAGNOSTIC-ONLY",
                (englishCatalog?.GetString("PackagingThreadGeometryBuilder.Error.CoreProfileCreationFailed", CultureInfo.InvariantCulture) ?? string.Empty).Contains("technical details were written") &&
                (englishCatalog?.GetString("PackagingThreadGeometryBuilder.Error.CoreCoilCreationFailed", CultureInfo.InvariantCulture) ?? string.Empty).Contains("technical details were written") &&
                (englishCatalog?.GetString("PackagingThreadGeometryBuilder.Error.CoreCoilVerificationFailed", CultureInfo.InvariantCulture) ?? string.Empty).Contains("Technical details were written") &&
                !((englishCatalog?.GetString("PackagingThreadGeometryBuilder.Error.CoreProfileCreationFailed", CultureInfo.InvariantCulture) ?? string.Empty).Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))),
                ref pass, ref fail, "Core-stage UI summaries are resource-backed; detailed exception strings remain in diagnostics and inner exceptions.");
            Check("THREAD-WALL-PREFLIGHT-REFUSALS-USE-RESOURCE-SUMMARY-AND-KEEP-DETAILS",
                (russianCatalog?.GetString("ThreadWallThickness.Error.PreflightRejected", CultureInfo.InvariantCulture) ?? string.Empty).Contains("Модель не изменена") &&
                (englishCatalog?.GetString("ThreadWallThickness.Error.PreflightRejected", CultureInfo.InvariantCulture) ?? string.Empty).Contains("model was not changed") &&
                !((englishCatalog?.GetString("ThreadWallThickness.Error.PreflightRejected", CultureInfo.InvariantCulture) ?? string.Empty).Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))),
                ref pass, ref fail, "Preflight refusal summaries must be bilingual while the guard retains the exact reason in DiagnosticDetail.");

            string cylindricalWallTooThinRu = russianCatalog == null ? null : russianCatalog.GetString("ThreadWallThickness.Error.CylindricalWallTooThin", CultureInfo.InvariantCulture);
            string cylindricalWallTooThinEn = englishCatalog == null ? null : englishCatalog.GetString("ThreadWallThickness.Error.CylindricalWallTooThin", CultureInfo.InvariantCulture);
            string taperedWallTooThinRu = russianCatalog == null ? null : russianCatalog.GetString("ThreadWallThickness.Error.TaperedWallTooThin", CultureInfo.InvariantCulture);
            string taperedWallTooThinEn = englishCatalog == null ? null : englishCatalog.GetString("ThreadWallThickness.Error.TaperedWallTooThin", CultureInfo.InvariantCulture);
            string runoutSpanRu = russianCatalog == null ? null : russianCatalog.GetString("PackagingThreadGeometryBuilder.Error.RunoutSpanTooShort", CultureInfo.InvariantCulture);
            string runoutSpanEn = englishCatalog == null ? null : englishCatalog.GetString("PackagingThreadGeometryBuilder.Error.RunoutSpanTooShort", CultureInfo.InvariantCulture);
            Check("FIX49-WALL-REFUSALS-SHOW-MEASURED-AND-REQUIRED-VALUES-IN-RU-EN",
                !string.IsNullOrWhiteSpace(cylindricalWallTooThinRu) && cylindricalWallTooThinRu.Contains("{1:0.###}") && cylindricalWallTooThinRu.Contains("{3:0.###}") && cylindricalWallTooThinRu.Contains("Уменьшите шаг") &&
                !string.IsNullOrWhiteSpace(cylindricalWallTooThinEn) && cylindricalWallTooThinEn.Contains("{1:0.###}") && cylindricalWallTooThinEn.Contains("{3:0.###}") && cylindricalWallTooThinEn.Contains("Reduce pitch") &&
                !string.IsNullOrWhiteSpace(taperedWallTooThinRu) && taperedWallTooThinRu.Contains("{9}") && taperedWallTooThinRu.Contains("Небезопасных контрольных точек") &&
                !string.IsNullOrWhiteSpace(taperedWallTooThinEn) && taperedWallTooThinEn.Contains("{9}") && taperedWallTooThinEn.Contains("Unsafe sample points") &&
                !cylindricalWallTooThinEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !taperedWallTooThinEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "Thin-wall refusals must expose the measured wall, required remaining wall and corrective action in both languages.");
            Check("FIX49-CAP-NECK-SHORT-RUNOUT-SPAN-IS-TYPED-AND-ACTIONABLE",
                (int)HelivantaErrorCode.PackagingThreadRunoutSpanTooShort == 33 &&
                !string.IsNullOrWhiteSpace(runoutSpanRu) && runoutSpanRu.Contains("требуется не менее") &&
                runoutSpanRu.Contains("{0:0.###}") && runoutSpanRu.Contains("Выберите более длинную цилиндрическую поверхность") &&
                !string.IsNullOrWhiteSpace(runoutSpanEn) && runoutSpanEn.Contains("{0:0.###}") && runoutSpanEn.Contains("Select a longer cylindrical surface") &&
                !runoutSpanEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "The short cap/neck cylinder check must display available and required spans with a direct corrective action.");

            string taperedRunoutFailureRu = russianCatalog == null ? null : russianCatalog.GetString("NativeTaperedThreadBuilder.Error.RunoutSegmentCreationFailed", CultureInfo.InvariantCulture);
            string taperedRunoutFailureEn = englishCatalog == null ? null : englishCatalog.GetString("NativeTaperedThreadBuilder.Error.RunoutSegmentCreationFailed", CultureInfo.InvariantCulture);
            string taperedPhysicalGrooveRu = russianCatalog == null ? null : russianCatalog.GetString("NativeTaperedThreadBuilder.Error.PhysicalGrooveCreationFailed", CultureInfo.InvariantCulture);
            string taperedPhysicalGrooveEn = englishCatalog == null ? null : englishCatalog.GetString("NativeTaperedThreadBuilder.Error.PhysicalGrooveCreationFailed", CultureInfo.InvariantCulture);
            Check("FIX49-TAPERED-RUNOUT-SEGMENT-FAILURE-IDENTIFIES-STAGE-AND-ACTION",
                (int)HelivantaErrorCode.TaperedRunoutSegmentCreationFailed == 34 &&
                !string.IsNullOrWhiteSpace(taperedRunoutFailureRu) && taperedRunoutFailureRu.Contains("{0}") && taperedRunoutFailureRu.Contains("{1}") && taperedRunoutFailureRu.Contains("укоротить резьбу") &&
                !string.IsNullOrWhiteSpace(taperedRunoutFailureEn) && taperedRunoutFailureEn.Contains("{0}") && taperedRunoutFailureEn.Contains("{1}") && taperedRunoutFailureEn.Contains("shortening the thread") &&
                !string.IsNullOrWhiteSpace(taperedPhysicalGrooveRu) && taperedPhysicalGrooveRu.Contains("непрерывной конической поверхности") &&
                !string.IsNullOrWhiteSpace(taperedPhysicalGrooveEn) && taperedPhysicalGrooveEn.Contains("continuous conical surface") &&
                !taperedRunoutFailureEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !taperedPhysicalGrooveEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "A failed tapered-thread run-out segment must identify the failing segment and offer concrete geometry corrections in both languages.");

            string taperedRunoutFallbackWarningRu = russianCatalog == null ? null : russianCatalog.GetString(
                "NativeTaperedThreadBuilder.Warning.RunoutFallback", CultureInfo.InvariantCulture);
            string taperedRunoutFallbackWarningEn = englishCatalog == null ? null : englishCatalog.GetString(
                "NativeTaperedThreadBuilder.Warning.RunoutFallback", CultureInfo.InvariantCulture);
            Check("FIX56-TAPERED-RUNOUT-FALLBACK-WARNING-IS-ACTIONABLE-AND-RU-EN-LOCALIZED",
                !string.IsNullOrWhiteSpace(taperedRunoutFallbackWarningRu) &&
                taperedRunoutFallbackWarningRu.Contains("резервный сегментный Coil") &&
                taperedRunoutFallbackWarningRu.Contains("Проверьте равномерность канавки") &&
                !string.IsNullOrWhiteSpace(taperedRunoutFallbackWarningEn) &&
                taperedRunoutFallbackWarningEn.Contains("segmented Coil fallback") &&
                taperedRunoutFallbackWarningEn.Contains("Check groove uniformity") &&
                !taperedRunoutFallbackWarningEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "The tapered run-out fallback warning must explain the fallback and ask the user to inspect groove uniformity in both languages.");

            string capNeckPreviewRu = russianCatalog == null ? null : russianCatalog.GetString("PackagingThreadDialog.Preview.Button", CultureInfo.InvariantCulture);
            string capNeckPreviewEn = englishCatalog == null ? null : englishCatalog.GetString("PackagingThreadDialog.Preview.Button", CultureInfo.InvariantCulture);
            string obsoleteEdgePromptRu = russianCatalog == null ? "not-loaded" : russianCatalog.GetString("PackagingThreadDialog.Label.EdgePrompt", CultureInfo.InvariantCulture);
            string obsoleteSafetyNoteRu = russianCatalog == null ? "not-loaded" : russianCatalog.GetString("PackagingThreadDialog.Label.VentSafetyNote", CultureInfo.InvariantCulture);
            string obsoleteEdgePromptEn = englishCatalog == null ? "not-loaded" : englishCatalog.GetString("PackagingThreadDialog.Label.EdgePrompt", CultureInfo.InvariantCulture);
            string obsoleteSafetyNoteEn = englishCatalog == null ? "not-loaded" : englishCatalog.GetString("PackagingThreadDialog.Label.VentSafetyNote", CultureInfo.InvariantCulture);
            Check("CAP-NECK-DIALOG-REMOVES-REDUNDANT-LABELS-AND-SHORTENS-PREVIEW-CAPTION",
                russianCatalog != null && englishCatalog != null &&
                capNeckPreviewRu == "Предпросмотр резьбы" && capNeckPreviewEn == "Thread preview" &&
                obsoleteEdgePromptRu == null && obsoleteSafetyNoteRu == null &&
                obsoleteEdgePromptEn == null && obsoleteSafetyNoteEn == null,
                ref pass, ref fail, "The cap/neck dialog must not include the redundant selection prompt or experimental-safety paragraph in either catalog, and its preview caption must stay short in both languages.");

            string capNeckHelpTitleRu = russianCatalog == null ? null : russianCatalog.GetString("Help.Inline.CapNeckThread.Title", CultureInfo.InvariantCulture);
            string capNeckHelpTitleEn = englishCatalog == null ? null : englishCatalog.GetString("Help.Inline.CapNeckThread.Title", CultureInfo.InvariantCulture);
            Check("INLINE-HELP-GLYPH-IDENTITY-USES-STABLE-TOPIC-SLUG",
                capNeckHelpTitleRu != null && capNeckHelpTitleEn != null &&
                HelivantaInlineHelpTopicIdentity.ResolveSlug(capNeckHelpTitleRu, "CapNeckThread", capNeckHelpTitleRu, capNeckHelpTitleEn, capNeckHelpTitleEn) == "CapNeckThread" &&
                HelivantaInlineHelpTopicIdentity.ResolveSlug(capNeckHelpTitleEn, "CapNeckThread", capNeckHelpTitleRu, capNeckHelpTitleEn, capNeckHelpTitleEn) == "CapNeckThread" &&
                HelivantaInlineHelpTopicIdentity.ResolveSlug("unknown title", "CapNeckThread", capNeckHelpTitleRu, capNeckHelpTitleEn, capNeckHelpTitleEn) == string.Empty,
                ref pass, ref fail, "The inline-help glyph identity must be stable across RU/EN captions and unknown captions must safely fall back.");

            string gostHelpTitleRu = russianCatalog == null ? null : russianCatalog.GetString("Help.Inline.GostMetricThread.Title", CultureInfo.InvariantCulture);
            string gostHelpTitleEn = englishCatalog == null ? null : englishCatalog.GetString("Help.Inline.GostMetricThread.Title", CultureInfo.InvariantCulture);
            string gostHelpBodyRu = russianCatalog == null ? null : russianCatalog.GetString("Help.Inline.GostMetricThread.Body", CultureInfo.InvariantCulture);
            string gostHelpBodyEn = englishCatalog == null ? null : englishCatalog.GetString("Help.Inline.GostMetricThread.Body", CultureInfo.InvariantCulture);
            Check("GOST-METRIC-HELP-RU-EN-COMPLETE",
                !string.IsNullOrWhiteSpace(gostHelpTitleRu) && gostHelpTitleRu.Contains("ГОСТ") &&
                !string.IsNullOrWhiteSpace(gostHelpTitleEn) && gostHelpTitleEn.Contains("GOST") &&
                !string.IsNullOrWhiteSpace(gostHelpBodyRu) && gostHelpBodyRu.Contains("ГОСТ 8724-2002") &&
                gostHelpBodyRu.Contains("ГОСТ 9150-2002") && gostHelpBodyRu.Contains("ГОСТ 24705-2004") &&
                gostHelpBodyRu.Contains("формулам 4.2") &&
                !string.IsNullOrWhiteSpace(gostHelpBodyEn) && gostHelpBodyEn.Contains("GOST 8724-2002") &&
                gostHelpBodyEn.Contains("GOST 9150-2002") && gostHelpBodyEn.Contains("GOST 24705-2004") &&
                gostHelpBodyEn.Contains("clause 4.2") &&
                !gostHelpBodyEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                HelivantaInlineHelpTopicIdentity.ResolveSlug(gostHelpTitleRu, "GostMetricThread", gostHelpTitleRu, gostHelpTitleEn, gostHelpTitleEn) == "GostMetricThread" &&
                HelivantaInlineHelpTopicIdentity.ResolveSlug(gostHelpTitleEn, "GostMetricThread", gostHelpTitleRu, gostHelpTitleEn, gostHelpTitleEn) == "GostMetricThread",
                ref pass, ref fail, "Статья ГОСТ должна иметь полный RU/EN текст и стабильную идентичность темы при смене языка.");

            string gostDefaultStandardRu = russianCatalog == null ? null : russianCatalog.GetString("Settings.Standard.Gost", CultureInfo.InvariantCulture);
            string gostDefaultStandardEn = englishCatalog == null ? null : englishCatalog.GetString("Settings.Standard.Gost", CultureInfo.InvariantCulture);
            Check("GOST-DEFAULT-STANDARD-SETTING-LABEL-RU-EN", gostDefaultStandardRu == "ГОСТ" && gostDefaultStandardEn == "GOST",
                ref pass, ref fail, "Настройка стандарта по умолчанию показывает локализованную подпись при сохранении стабильного ключа GOST.");

            string russianNoticeOk = russianCatalog == null ? null : russianCatalog.GetString("Common.Button.OK", CultureInfo.InvariantCulture);
            string englishNoticeOk = englishCatalog == null ? null : englishCatalog.GetString("Common.Button.OK", CultureInfo.InvariantCulture);
            Check("THEMED-NOTICE-OK-LABEL-HAS-RU-EN-VALUES",
                russianNoticeOk == "ОК" && englishNoticeOk == "OK",
                ref pass, ref fail, "The shared themed acknowledgement dialog must obtain its OK label from both embedded catalogs.");
            string[] noticeSeverityKeys = { "Common.Notice.Error", "Common.Notice.Warning", "Common.Notice.Question", "Common.Notice.Information" };
            bool noticeSeverityResourcesComplete = russianCatalog != null && englishCatalog != null && noticeSeverityKeys.All(key =>
                !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            bool noticeSeverityEnglishHasCyrillic = englishCatalog == null || noticeSeverityKeys.Any(key =>
                (englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("THEMED-NOTICE-SEVERITY-LABELS-HAVE-RU-EN-VALUES", noticeSeverityResourcesComplete && !noticeSeverityEnglishHasCyrillic,
                ref pass, ref fail, "The shared notification dialog's accessible severity labels must be complete in both embedded catalogs and English must not contain Cyrillic.");

            string[] threadIntegrityResourceKeys = {
                "ThreadIntegrity.Risk.ScanIncomplete", "ThreadIntegrity.Risk.Critical",
                "ThreadIntegrity.Risk.Attention", "ThreadIntegrity.Risk.Low",
                "ThreadIntegrity.NextAction.ScanIncomplete", "ThreadIntegrity.NextAction.Errors",
                "ThreadIntegrity.NextAction.Warnings", "ThreadIntegrity.NextAction.Clear",
                "ThreadIntegrity.Standard.CapNeck", "ThreadIntegrity.Standard.ISO",
                "ThreadIntegrity.Standard.BSW", "ThreadIntegrity.Standard.ANSI",
                "ThreadIntegrity.Standard.Tr", "ThreadIntegrity.Standard.S", "ThreadIntegrity.Standard.Rd",
                "ThreadIntegrity.Standard.NativeThread", "ThreadIntegrity.Standard.UnknownInventor",
                "ThreadIntegrity.DisplayLabel.NativeThread", "ThreadIntegrity.Native.ParametersUnavailable",
                "ThreadIntegrity.Native.Parameters.Known", "ThreadIntegrity.Native.Parameters.KnownInterval",
                "ThreadIntegrity.Native.Parameters.Unmatched", "ThreadIntegrity.Native.Parameters.ReadFailed",
                "ThreadIntegrity.Finding.MetadataGroupLoadFailed", "ThreadIntegrity.Export.Filter",
                "NativeThread.Error.NoActivePart", "NativeThread.Error.EnumerationFailed",
                "NativeThread.Error.ParametersUnavailable", "NativeThread.Error.ScanIncomplete",
                "NativeThread.Error.FaceInspectionFailed", "NativeThread.Error.IntervalOverlapReason",
                "NativeThread.Error.IntervalUnknownReason", "NativeThread.Error.OverlapBlocked",
                "NativeThread.Interval.Detected", "NativeThread.Interval.UnknownDetail"
            };
            bool integrityKeysPresent = russianCatalog != null && englishCatalog != null &&
                threadIntegrityResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            Check("THREAD-INTEGRITY-RU-EN-RESOURCE-KEYS-COMPLETE", integrityKeysPresent,
                ref pass, ref fail, "All thread-integrity status, recommendation, standards, and native-thread parameter templates must exist in both embedded catalogs.");
            bool englishIntegrityHasCyrillic = englishCatalog == null || threadIntegrityResourceKeys.Any(key =>
            {
                string value = englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                return value.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            });
            Check("THREAD-INTEGRITY-EN-RESOURCE-VALUES-DO-NOT-CONTAIN-CYRILLIC", !englishIntegrityHasCyrillic,
                ref pass, ref fail, "Embedded English thread-integrity resources must not fall back to Russian text.");

            string[] threadDialogStatusResourceKeys = {
                "ThreadDialog.Status.Selection.WaitingForEdgesInInventor", "ThreadDialog.Status.Preview.ActiveDocumentChanged",
                "ThreadDialog.Status.Mode.DisableCapNeckBeforeTaper", "ThreadDialog.Status.Mode.CloseCylindricalPreviewBeforeTaper",
                "ThreadDialog.Status.Mode.TaperAutoPreparation", "ThreadDialog.Status.Mode.Cylindrical",
                "ThreadDialog.Status.Preview.CleanupFailedOnClose", "ThreadDialog.Status.Edit.CapNeckParametersLoaded",
                "ThreadDialog.Status.Edit.ParametersLoaded", "ThreadDialog.Status.NativeConversion.Initial",
                "ThreadDialog.Status.NativeConversion.NextLoadFailed", "ThreadDialog.Status.NativeConversion.NextSelectionCancelled",
                "ThreadDialog.Status.Edit.SelectionCancelled", "ThreadDialog.Status.Edit.WaitingForThreadSelection",
                "ThreadDialog.Status.Mode.CreateThread", "ThreadDialog.Status.NativeConversion.SettingsRestored",
                "ThreadDialog.Status.Edit.UnsavedChangesReset", "ThreadDialog.Status.Selection.ParametersAndEdgesReset",
                "ThreadDialog.Status.NativeConversion.RedoRestored", "ThreadDialog.Status.Build.UndoRestored",
                "ThreadDialog.Status.Build.UndoEdgesNotRestored", "ThreadDialog.Status.Selection.WaitingForEdges",
                "ThreadDialog.Status.Mode.TaperLayoutRefreshFailed", "ThreadDialog.Status.Taper.NonStandardLength",
                "ThreadDialog.Status.Preview.DisableFailed", "ThreadDialog.Status.Preview.Hidden",
                "ThreadDialog.Status.Preview.HiddenDuringNativeConversion",
                "ThreadDialog.Status.Preview.HiddenForBuild",
                "ThreadDialog.Status.Preview.UnavailableAfterConversion",
                "ThreadDialog.Status.Preview.SelectCircularEdge",

                "ThreadDialog.Status.Preview.CleanupFailedBeforeContinue", "ThreadDialog.Status.Edit.GroupNotSet",
                "ThreadDialog.Status.Input.InvalidNominalDiameterPitch", "ThreadDialog.Status.Edit.CannotConvertTaperedToCylindrical",
                "ThreadDialog.Status.Edit.Cancelled", "ThreadDialog.Status.Edit.ValidatingAndReplacing",
                "ThreadDialog.Status.Edit.FailedSeeInfo", "ThreadDialog.Status.NativeConversion.PlanLost",
                "ThreadDialog.Status.NativeConversion.FailedSeeInfo", "ThreadDialog.Status.NativeConversion.FailedCheckModelBeforeSave",
                "ThreadDialog.Status.NativeConversion.WaitingForNextThread", "ThreadDialog.Status.NativeConversion.NextSelectionUnavailable",
                "ThreadDialog.Status.NativeConversion.SelectSetClearFailed",
                "ThreadDialog.Status.Build.SelectStartCircularEdges", "ThreadDialog.Status.Input.EnterNominalDiameterPitch",
                "ThreadDialog.Status.Taper.SelectStandardAndDesignation", "ThreadDialog.Status.Taper.SingleStartEdgePerOperation",
                "ThreadDialog.Status.Build.FailedSeeInfo"
            };
            bool threadDialogStatusKeysPresent = russianCatalog != null && englishCatalog != null &&
                threadDialogStatusResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            Check("THREAD-DIALOG-STATUS-RESOURCE-KEYS-COMPLETE", threadDialogStatusKeysPresent,
                ref pass, ref fail, "Migrated runtime statuses must have stable RU/EN resource entries.");
            bool threadDialogStatusEnglishHasCyrillic = englishCatalog == null || threadDialogStatusResourceKeys.Any(key =>
            {
                string value = englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                return value.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            });
            Check("THREAD-DIALOG-STATUS-EN-VALUES-DO-NOT-CONTAIN-CYRILLIC", !threadDialogStatusEnglishHasCyrillic,
                ref pass, ref fail, "English main-dialog statuses must render as complete English sentences.");

            string[] taperedWarningResourceKeys = {
                "ThreadDialog.Taper.NonStandardLengthTitle", "ThreadDialog.Taper.NonStandardLengthBody",
                "ThreadDialog.Taper.NonStandardLengthReason.Manual", "ThreadDialog.Taper.NonStandardLengthReason.FullSurface",
                "ThreadDialog.Taper.GeometryConfirmFallback"
            };
            bool taperedWarningResourcesValid = russianCatalog != null && englishCatalog != null &&
                taperedWarningResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture))) &&
                (englishCatalog.GetString("ThreadDialog.Taper.NonStandardLengthBody", CultureInfo.InvariantCulture) ?? string.Empty).Contains("{0}") &&
                !taperedWarningResourceKeys.Any(key => (englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("ITERATION12-FIX7-FIX8-TAPER-WARNINGS-HAVE-COMPLETE-RU-EN-RESOURCES",
                taperedWarningResourcesValid, ref pass, ref fail,
                "Nonstandard tapered-length notices and geometry-confirmation fallback must use complete semantic RU/EN resources.");

            string[] modeSwitchWarningResourceKeys = {
                "ThreadDialog.ModeSwitch.WarningTitle",
                "ThreadDialog.ModeSwitch.TaperedBlockedByCapNeck",
                "ThreadDialog.ModeSwitch.CapNeckBlockedByTapered",
                "ThreadDialog.ModeSwitch.CapNeckEditMustBeEnded"
            };
            bool modeSwitchWarningResourcesPresent = russianCatalog != null && englishCatalog != null &&
                modeSwitchWarningResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)));
            Check("THREAD-MODE-SWITCH-WARNINGS-HAVE-RU-EN-RESOURCE-KEYS", modeSwitchWarningResourcesPresent,
                ref pass, ref fail, "Both conflicting-mode transition warnings and their title must exist in embedded RU/EN catalogs.");
            bool modeSwitchWarningEnglishHasCyrillic = englishCatalog == null || modeSwitchWarningResourceKeys.Any(key =>
            {
                string value = englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                return value.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            });
            Check("THREAD-MODE-SWITCH-WARNINGS-EN-VALUES-DO-NOT-CONTAIN-CYRILLIC", !modeSwitchWarningEnglishHasCyrillic,
                ref pass, ref fail, "The English themed warnings must render as full English text with no Russian fallback.");
            string[] structuredBuildErrorResourceKeys = {
                "ThreadBuilder.Error.TaperedFolderRepairIncomplete",
                "ThreadBuilder.Error.FallbackFolderGroupingIncomplete",
                "ThreadBuilder.Error.UnsupportedFeatureOperation"
            };
            bool structuredBuildErrorResourcesValid = russianCatalog != null && englishCatalog != null &&
                structuredBuildErrorResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture))) &&
                !structuredBuildErrorResourceKeys.Any(key => (englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("ITERATION12-FIX7-FIX9-STRUCTURED-BUILD-ERRORS-HAVE-SEMANTIC-RU-EN-RESOURCES",
                structuredBuildErrorResourcesValid, ref pass, ref fail,
                "Known folder-repair and unsupported-feature errors must use complete semantic RU/EN resources without English fallback to Cyrillic.");

            Check("TAPER-SUPPORT-FILTERS-ONLY-HIDDEN-TAGGED-MATCH", TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "cone-thread-id", "cone-thread-id", true, "NPT", "npt", "1/2", "1/2-14 NPT", "1/2-14 NPT"),
                ref pass, ref fail, "Из отчёта исключается только скрытая TaperedThreadInfo с тем же ThreadId, стандартом и полным обозначением группы Helivanta.");
            Check("TAPER-SUPPORT-FILTER-KEEPS-VISIBLE-NATIVE", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                true, true, true, "cone-thread-id", "cone-thread-id", true, "NPT", "NPT", "1/2", "1/2-14 NPT", "1/2-14 NPT"),
                ref pass, ref fail, "Видимая штатная коническая резьба Inventor остаётся в отчёте даже при совпадении обозначения.");
            Check("TAPER-SUPPORT-FILTER-KEEPS-UNTAGGED", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, false, "", "cone-thread-id", true, "NPT", "NPT", "1/2", "1/2-14 NPT", "1/2-14 NPT"),
                ref pass, ref fail, "Скрытая штатная резьба без подтверждённых метаданных Helivanta не фильтруется.");
            Check("TAPER-SUPPORT-FILTER-KEEPS-DIFFERENT-THREAD-ID", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "other-id", "cone-thread-id", true, "NPT", "NPT", "1/2", "1/2-14 NPT", "1/2-14 NPT"),
                ref pass, ref fail, "Другое скрытое коническое обозначение с собственным ThreadId остаётся в отчёте.");
            Check("TAPER-SUPPORT-FILTER-KEEPS-DIFFERENT-DESIGNATION", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "cone-thread-id", "cone-thread-id", true, "NPT", "NPT", "1/2", "1/2-14 NPT", "3/4-14 NPT"),
                ref pass, ref fail, "Несовпадение табличного обозначения не позволяет скрыть штатную резьбу из проверки.");
            Check("TAPER-SUPPORT-FILTER-KEEPS-NON-TAPERED", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, false, true, "cone-thread-id", "cone-thread-id", true, "NPT", "NPT", "1/2", "1/2-14 NPT", "1/2-14 NPT"),
                ref pass, ref fail, "Обычная StandardThreadInfo не исключается правилами для конического support feature.");
            Check("TAPER-SUPPORT-GOST-HIDES-EXACT-JIS-CARRIER", TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "gost-cone-id", "gost-cone-id", true,
                "GOST 6211-81", "JIS Taper External", "R 1/2", "R 1/2", "1/2-14 BSPT",
                "JIS Taper", "1/2-14 BSPT", true),
                ref pass, ref fail, "Для GOST R/Rc скрывается только JIS Taper carrier с тем же ThreadId и точной нативной designation.");
            Check("TAPER-SUPPORT-GOST-KEEPS-DIFFERENT-JIS-DESIGNATION", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "gost-cone-id", "gost-cone-id", true,
                "GOST 6211-81", "JIS Taper External", "R 1/2", "R 1/2", "3/4-14 BSPT",
                "JIS Taper", "1/2-14 BSPT", true),
                ref pass, ref fail, "Другая JIS Taper designation не скрывается даже при совпадающем GOST ThreadId.");
            Check("TAPER-SUPPORT-GOST-KEEPS-WRONG-CARRIER", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "gost-cone-id", "gost-cone-id", true,
                "GOST 6211-81", "NPT", "R 1/2", "R 1/2", "1/2-14 NPT",
                "JIS Taper", "1/2-14 BSPT", true),
                ref pass, ref fail, "NPT не может быть перепутан со скрытой носительской операцией JIS Taper для GOST R/Rc.");
            Check("TAPER-SUPPORT-GOST-REJECTS-NATIVE-CALL-OUT-CHANGE", !TaperedThreadSupportPolicy.IsMatchingHiddenSupportFeature(
                false, true, true, "gost-cone-id", "gost-cone-id", true,
                "GOST 6211-81", "JIS Taper Internal", "Rc 1/2", "Rc 1/2", "1/2-14 BSPT",
                "JIS Taper", "1/2-14-BSPT-LH", true),
                ref pass, ref fail, "Нативное designation support feature должно совпадать с сохранённой записью метаданных.");

            double taperRatio;
            Check("TAPER-STANDARD-NPT-RATIO", TaperedThreadGeometryPolicy.TryGetDiameterRatio("NPT", out taperRatio) &&
                Math.Abs(taperRatio - 1.0 / 16.0) < 1e-12, ref pass, ref fail,
                "NPT использует диаметральную конусность 1:16.");
            Check("TAPER-STANDARD-JIS-RATIO", TaperedThreadGeometryPolicy.TryGetDiameterRatio("JIS Taper", out taperRatio) &&
                Math.Abs(taperRatio - 1.0 / 16.0) < 1e-12, ref pass, ref fail,
                "JIS Taper использует диаметральную конусность 1:16.");
            Check("TAPER-STANDARD-UNKNOWN-REJECT", !TaperedThreadGeometryPolicy.TryGetDiameterRatio("ISO Metric", out taperRatio),
                ref pass, ref fail, "Неподдерживаемый стандарт не получает конусность по догадке.");
            Check("TAPER-ANGLE-NPT-60", TaperedThreadGeometryPolicy.TryGetThreadAngleDegrees("NPT", out double nptAngle) && Math.Abs(nptAngle - 60.0) < 1e-12,
                ref pass, ref fail, "Физический профиль NPT использует включённый угол 60°.");
            Check("TAPER-ANGLE-JIS-55", TaperedThreadGeometryPolicy.TryGetThreadAngleDegrees("JIS Taper", out double jisAngle) && Math.Abs(jisAngle - 55.0) < 1e-12,
                ref pass, ref fail, "Физический профиль JIS Taper использует включённый угол 55°.");
            Check("TAPER-ANGLE-UNKNOWN-REJECT", !TaperedThreadGeometryPolicy.TryGetThreadAngleDegrees("ISO Metric", out double unknownAngle),
                ref pass, ref fail, "Неизвестный угол профиля не подменяется предположением.");
            Check("TAPER-COIL-SIGN-EXTERNAL-GROWS", TaperedThreadGeometryPolicy.TryGetCoilTaperAngleRadians(1.0 / 16.0, true, out double externalCoilTaperAngle) &&
                externalCoilTaperAngle > 0.0 && Math.Abs(externalCoilTaperAngle - Math.Atan(1.0 / 32.0)) < 1e-12,
                ref pass, ref fail, "Физическая наружная спираль использует знак угла, соответствующий увеличению радиуса от выбранного малого торца к плечу.");
            Check("TAPER-COIL-SIGN-INTERNAL-SHRINKS", TaperedThreadGeometryPolicy.TryGetCoilTaperAngleRadians(1.0 / 16.0, false, out double internalCoilTaperAngle) &&
                internalCoilTaperAngle < 0.0 && Math.Abs(internalCoilTaperAngle + Math.Atan(1.0 / 32.0)) < 1e-12,
                ref pass, ref fail, "Физическая внутренняя спираль использует противоположный знак для сужения отверстия внутрь.");
            Check("TAPER-COIL-SIGN-INVALID-REJECT", !TaperedThreadGeometryPolicy.TryGetCoilTaperAngleRadians(double.NaN, true, out double invalidCoilTaperAngle),
                ref pass, ref fail, "Некорректная конусность не даёт построить спираль с углом по умолчанию.");
            Check("TAPER-RUNOUT-END-SHORTENS-CORE", TaperedThreadGeometryPolicy.TryGetRunoutStationsCm(
                0.0, 2.0, 0.2, 1.0, false, true, out double runoutEndCoreStart, out double runoutEndCoreEnd,
                out double runoutEndLength, out string runoutEndError) &&
                Math.Abs(runoutEndLength - 0.2) < 1e-12 && Math.Abs(runoutEndCoreStart) < 1e-12 &&
                Math.Abs(runoutEndCoreEnd - 1.8) < 1e-12,
                ref pass, ref fail, "Один конечный сбег укорачивает полноразмерную часть ровно на min(число шагов, 45% интервала).");
            Check("TAPER-RUNOUT-BOTH-ENDS-KEEP-CORE", TaperedThreadGeometryPolicy.TryGetRunoutStationsCm(
                1.0, 3.0, 0.2, 2.0, true, true, out double runoutBothStart, out double runoutBothEnd,
                out double runoutBothLength, out string runoutBothError) &&
                runoutBothStart > 1.0 && runoutBothEnd < 3.0 && runoutBothEnd - runoutBothStart > 0.2,
                ref pass, ref fail, "Два сбега оставляют между собой положительную полноразмерную винтовую часть.");
            Check("TAPER-RUNOUT-NO-FADE-PRESERVES-INTERVAL", TaperedThreadGeometryPolicy.TryGetRunoutStationsCm(
                0.5, 2.5, 0.2, 1.0, false, false, out double runoutNoneStart, out double runoutNoneEnd,
                out double runoutNoneLength, out string runoutNoneError) &&
                Math.Abs(runoutNoneStart - 0.5) < 1e-12 && Math.Abs(runoutNoneEnd - 2.5) < 1e-12 && runoutNoneLength == 0.0,
                ref pass, ref fail, "Если нет ни начального, ни конечного сбега, полная длина конической винтовой части сохраняется.");
            Check("TAPER-RUNOUT-TOO-SHORT-REJECT", !TaperedThreadGeometryPolicy.TryGetRunoutStationsCm(
                0.0, 0.18, 0.2, 1.0, false, true, out double runoutShortStart, out double runoutShortEnd,
                out double runoutShortLength, out string runoutShortError),
                ref pass, ref fail, "Слишком короткий участок отклоняется вместо схлопывания основного Coil.");
            Check("TAPER-RUNOUT-ZERO-STEPS-REJECT", !TaperedThreadGeometryPolicy.TryGetRunoutStationsCm(
                0.0, 2.0, 0.2, 0.0, false, true, out double runoutZeroStart, out double runoutZeroEnd,
                out double runoutZeroLength, out string runoutZeroError),
                ref pass, ref fail, "Запрошенный сбег с нулевым числом шагов отклоняется до начала геометрических операций.");
            Check("TAPER-RUNOUT-DENSITY-SMOOTH-BOUNDED", TaperedThreadGeometryPolicy.TryGetRunoutSectionsPerTurn(
                1.0, 0.01, out int smoothRunoutSections, out string smoothRunoutDensityError) &&
                smoothRunoutSections >= 14 && smoothRunoutSections <= 18,
                ref pass, ref fail, "Единственный поддерживаемый конический сбег использует ограниченную плотность 14–18 сечений на оборот.");
            Check("TAPER-RUNOUT-DENSITY-INVALID-REJECT", !TaperedThreadGeometryPolicy.TryGetRunoutSectionsPerTurn(
                double.NaN, 0.01, out int invalidRunoutSections, out string invalidRunoutDensityError),
                ref pass, ref fail, "Некорректный локальный радиус отклоняется до создания Loft.");
            Check("TAPER-RUNOUT-TERMINAL-SAMPLING-START-FADE", TaperedThreadGeometryPolicy.TryGetRunoutSectionStationsCm(
                0.0, 1.08, 0.0, 1.0, 18, out double[] startFadeStations, out string startFadeStationError) &&
                startFadeStations.Length > 18 && startFadeStations.Length <= 257 &&
                startFadeStations.SequenceEqual(startFadeStations.OrderBy(value => value)) &&
                startFadeStations.Count(value => value >= 0.90 && value <= 1.0) >= 3,
                ref pass, ref fail, "В зоне терминального затухания стартового сбега добавлены упорядоченные сечения без изменения функции затухания.");
            Check("TAPER-RUNOUT-TERMINAL-SAMPLING-END-FADE", TaperedThreadGeometryPolicy.TryGetRunoutSectionStationsCm(
                -0.08, 1.0, 0.0, 1.0, 18, out double[] endFadeStations, out string endFadeStationError) &&
                endFadeStations.Length > 18 && endFadeStations.Length <= 257 &&
                endFadeStations.SequenceEqual(endFadeStations.OrderBy(value => value)) &&
                endFadeStations.Count(value => value >= 0.90 && value <= 1.0) >= 4,
                ref pass, ref fail, "В зоне терминального затухания конечного сбега добавлены упорядоченные сечения, включая конечную станцию.");
            Check("TAPER-RUNOUT-TERMINAL-SAMPLING-INVALID-REJECT", !TaperedThreadGeometryPolicy.TryGetRunoutSectionStationsCm(
                0.0, 1.0, 0.0, 1.0, 254, out double[] overBudgetStations, out string overBudgetStationError),
                ref pass, ref fail, "Количество базовых сечений, не оставляющее запас для терминального уплотнения, отклоняется заранее.");
            int fallbackRunoutSegments;
            string fallbackRunoutError;
            Check("TAPER-RUNOUT-FALLBACK-SMOOTH-BOUNDED", TaperedThreadGeometryPolicy.TryGetFallbackRunoutSegmentCount(
                0.6, 0.2, out fallbackRunoutSegments, out fallbackRunoutError) && fallbackRunoutSegments == 32,
                ref pass, ref fail, "Резервный сегментный Coil использует 12 сегментов на оборот, ограниченных максимумом 32.");
            Check("TAPER-RUNOUT-FALLBACK-INVALID-REJECT", !TaperedThreadGeometryPolicy.TryGetFallbackRunoutSegmentCount(
                0.0, 0.2, out fallbackRunoutSegments, out fallbackRunoutError),
                ref pass, ref fail, "Нулевая длина сбега отклоняется до создания резервных Coil.");
            double terminalPenetration;
            bool terminalIntersects;
            string terminalPenetrationError;
            Check("TAPER-RUNOUT-FALLBACK-TERMINAL-OUTSIDE-SKIP",
                TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.0108, 0.14, 0.048, 0.0014, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                !terminalIntersects && terminalPenetration <= 0.0,
                ref pass, ref fail, "Терминальный профиль, полностью вышедший за поверхность конуса, не должен становиться CoilFeature.");
            Check("TAPER-RUNOUT-FALLBACK-MATERIAL-CROSSING-ACCEPT",
                TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.0108, 0.14, 0.20, 0.0014, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                terminalIntersects && terminalPenetration > 0.0014,
                ref pass, ref fail, "Профиль с достаточным проникновением в материал по-прежнему проходит к созданию CoilFeature.");
            Check("TAPER-RUNOUT-FALLBACK-NEAR-ZERO-SLIVER-REJECT",
                TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.01, 0.10, 0.09545454545454546, 0.0005, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                !terminalIntersects && Math.Abs(terminalPenetration - 0.0005) < 1e-9,
                ref pass, ref fail, "Профиль с проникновением ровно на минимальном допуске пропускается, чтобы не создавать неустойчивый тонкий слепок.");
            Check("TAPER-RUNOUT-FALLBACK-ROUNDING-SLIVER-REJECT",
                TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.01, 0.10, 0.0954545459090909, 0.0005, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                terminalPenetration > 0.0005 && terminalPenetration - 0.0005 < 1e-10 && !terminalIntersects,
                ref pass, ref fail, "Проникновение, которое превышает порог только на величину меньше численного запаса, по-прежнему пропускается.");
            Check("TAPER-RUNOUT-FALLBACK-MARGIN-ACCEPT",
                TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.01, 0.10, 0.09545454727272727, 0.0005, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                terminalPenetration - 0.0005 > 1e-10 && terminalIntersects,
                ref pass, ref fail, "Профиль с проникновением выше порога и численного запаса не блокируется защитой от тонкого слепка.");
            Check("TAPER-RUNOUT-FALLBACK-PENETRATION-INVALID-REJECT",
                !TaperedThreadGeometryPolicy.TryGetFallbackRunoutMaterialPenetration(
                    -0.01, 0.10, 1.01, 0.0005, out terminalPenetration, out terminalIntersects, out terminalPenetrationError) &&
                !string.IsNullOrEmpty(terminalPenetrationError),
                ref pass, ref fail, "Недопустимый коэффициент fade отклоняется до создания геометрии.");
            Check("TAPER-CHAMFER-DISABLED-ACCEPTS-NO-CUT", TaperedThreadGeometryPolicy.TryValidateChamfer(
                0, 0.0, 0.0, 3.0, 1.0, 0.9, true, false, false, out string taperChamferOffError),
                ref pass, ref fail, "При выключенной конической фаске не требуется радиальная глубина фаски.");
            Check("TAPER-CHAMFER-START-ACCEPTS-VALID", TaperedThreadGeometryPolicy.TryValidateChamfer(
                1, 1.0, 0.0, 3.0, 1.0, 0.9, true, false, false, out string taperChamferStartError),
                ref pass, ref fail, "Наружная круговая фаска 1 мм проходит пред-проверку на валидном коническом интервале.");
            Check("TAPER-CHAMFER-BOTH-ENDS-FIT", TaperedThreadGeometryPolicy.TryValidateChamfer(
                3, 1.0, 0.0, 3.0, 1.0, 0.9, true, false, false, out string taperChamferBothError),
                ref pass, ref fail, "Две конические фаски разрешены, только если обе помещаются на интервале.");
            Check("TAPER-CHAMFER-REJECTS-TOO-LARGE-RADIUS", !TaperedThreadGeometryPolicy.TryValidateChamfer(
                1, 11.0, 0.0, 3.0, 1.0, 0.9, true, false, false, out string taperChamferLargeError),
                ref pass, ref fail, "Наружная фаска не может достигать или превышать радиус конуса.");
            Check("TAPER-CHAMFER-REJECTS-SHOULDER", !TaperedThreadGeometryPolicy.TryValidateChamfer(
                1, 1.0, 0.0, 3.0, 1.0, 0.9, true, true, false, out string taperChamferShoulderError),
                ref pass, ref fail, "Фаска у упорного/вогнутого ребра блокируется до модификации модели.");
            Check("TAPER-CHAMFER-REJECTS-BOTH-TOO-CLOSE", !TaperedThreadGeometryPolicy.TryValidateChamfer(
                3, 9.0, 0.0, 1.5, 1.0, 0.9, true, false, false, out string taperChamferBothShortError),
                ref pass, ref fail, "Две фаски не допускаются, если их суммарный размер не помещается на коническом участке.");
            Check("TAPER-FOLDER-NPT-NATIVE-NAME", ThreadFolderNamePolicy.BuildTaperedThreadFolderName("NPT", "1/2 - 14 NPT x 18.796mm", "1/2-14", 1, true) ==
                "Резьба — 1/2 - 14 NPT x 18.796mm · 1 заход · Правая (R)", ref pass, ref fail,
                "Все операции конической NPT получают одну папку с полным обозначением, возвращённым таблицей Inventor.");
            Check("TAPER-FOLDER-JIS-LEFT-FALLBACK", ThreadFolderNamePolicy.BuildTaperedThreadFolderName("JIS Taper", "", "PT1/2", 1, false) ==
                "Резьба — JIS Taper · PT1/2 · 1 заход · Левая (L)", ref pass, ref fail,
                "При отсутствии читаемого обозначения JIS папка использует ключ таблицы и сохраняет направление.");
            Check("PACKAGING-THREAD-FOLDER-NAME-IS-UNIQUE-AND-DESCRIPTIVE",
                ThreadFolderNamePolicy.BuildPackagingThreadFolderName(22.0, 2.7, 3, true, false) ==
                "Резьба крышек/горловин — наружная · D22 мм · P2.7 мм · 3 захода · Правая (R)" &&
                ThreadFolderNamePolicy.BuildPackagingThreadFolderName(22.0, 2.7, 1, false, true) ==
                "Резьба крышек/горловин — внутренняя · D22 мм · P2.7 мм · 1 заход · Левая (L)",
                ref pass, ref fail, "Имя папки резьбы крышек/горловин показывает тип поверхности, диаметр, шаг, число заходов и направление.");
            Check("TAPER-PITCH-NPT-27-TPI", TaperedThreadGeometryPolicy.TryResolvePitchAndDepthMm(false, 0.0, "27", 0.0296,
                out double taperPitchMmTest, out double taperDepthMmTest, out string taperPitchError) &&
                Math.Abs(taperPitchMmTest - 25.4 / 27.0) < 1e-9 && Math.Abs(taperDepthMmTest - 0.0296 * 25.4) < 1e-9,
                ref pass, ref fail, "Дюймовая NPT получает шаг 25,4/TPI и высоту профиля из ThreadTable, а не от старого ISO-поля.");
            Check("TAPER-PITCH-NPT-HALF-14-TPI", TaperedThreadGeometryPolicy.TryResolvePitchAndDepthMm(false, 0.0, "14", 0.05714,
                out double taperHalfPitchMm, out double taperHalfDepthMm, out string taperHalfPitchError) &&
                Math.Abs(taperHalfPitchMm - 25.4 / 14.0) < 1e-9 && Math.Abs(taperHalfDepthMm - 0.05714 * 25.4) < 1e-9,
                ref pass, ref fail, "1/2-14 NPT использует табличные 14 TPI (шаг ≈1,814 мм), а не оставшийся шаг M22×1,5.");
            Check("TAPER-PHYSICAL-GROOVE-FITS-PITCH", TaperedThreadGeometryPolicy.TryGetPhysicalGrooveDimensionsMm(25.4 / 27.0, 0.0296 * 25.4,
                out double taperTopWidthMmTest, out double taperRootWidthMmTest, out string taperGrooveError) &&
                taperTopWidthMmTest > taperRootWidthMmTest && taperRootWidthMmTest > 0.01 * (25.4 / 27.0),
                ref pass, ref fail, "Физический профиль NPT имеет положительную ширину впадины и помещается в шаг.");
            Check("TAPER-PHYSICAL-GROOVE-JIS-55", TaperedThreadGeometryPolicy.TryGetPhysicalGrooveDimensionsMm(25.4 / 27.0, 0.0296 * 25.4, 55.0,
                out double jisTopWidth, out double jisRootWidth, out string jisGrooveError) &&
                jisTopWidth > jisRootWidth && jisRootWidth > taperRootWidthMmTest,
                ref pass, ref fail, "При той же таблице высоты профиль 55° шире во впадине, чем профиль NPT 60°.");
            Check("TAPER-PHYSICAL-GROOVE-KEEPS-CREST-LAND", TaperedThreadGeometryPolicy.TryGetPhysicalGrooveDimensionsMm(25.4 / 11.5, 0.8 * (25.4 / 11.5), 60.0,
                out double nptLargeTopWidth, out double nptLargeRootWidth, out string nptLargeGrooveError) &&
                Math.Abs(nptLargeTopWidth / (25.4 / 11.5) - 0.962) < 1e-9 &&
                (25.4 / 11.5 - nptLargeTopWidth) >= 0.03 * (25.4 / 11.5) &&
                nptLargeRootWidth >= 0.035 * (25.4 / 11.5),
                ref pass, ref fail, "Физическая NPT-канавка не занимает почти весь шаг: остаётся заметная площадка вершины и положительная площадка впадины.");
            Check("TAPER-THIN-WALL-BLOCKS-STOCK-CHANGE-PLUS-GROOVE", !ThreadWallThicknessPolicy.HasSafeRemainingWall(0.35, -0.28, 0.1767, 0.22087,
                out double thinTaperRemainingCm, out double thinTaperRequiredCm) && thinTaperRemainingCm < thinTaperRequiredCm,
                ref pass, ref fail, "Коническая резьба блокируется, если предварительное изменение радиуса и профиль канавки оставят стенку ниже минимума.");
            Check("TAPER-THICK-WALL-ALLOWS-STANDARD-GROOVE", ThreadWallThicknessPolicy.HasSafeRemainingWall(0.60, -0.10, 0.11767, 0.22087,
                out double thickTaperRemainingCm, out double thickTaperRequiredCm) && thickTaperRemainingCm >= thickTaperRequiredCm,
                ref pass, ref fail, "Достаточная исходная стенка допускает стандартную глубину конического профиля.");
            Check("TAPER-WALL-CHAMFER-USES-MAX-LOCAL-DEPTH", Math.Abs(ThreadWallThicknessPolicy.EffectiveCutDepthCm(
                0.08, true, 0.12) - 0.12) < 1e-12 && Math.Abs(ThreadWallThicknessPolicy.EffectiveCutDepthCm(
                0.14, true, 0.12) - 0.14) < 1e-12,
                ref pass, ref fail, "Коническая проверка стенки учитывает максимум локальной глубины профиля/фаски, а не их сумму.");
            Check("TAPER-WALL-SOLID-SHAFT-RAY-CAPPED", Math.Abs(ThreadWallThicknessPolicy.EffectiveRadialWallThicknessCm(true, 5.2, 2.6) - 2.6) < 1e-12,
                ref pass, ref fail, "Для сплошного наружного вала луч через весь диаметр не выдаётся за толщину стенки: используется радиальная глубина до оси.");
            Check("TAPER-WALL-THIN-OUTER-TUBE-PRESERVED", Math.Abs(ThreadWallThicknessPolicy.EffectiveRadialWallThicknessCm(true, 0.18, 2.6) - 0.18) < 1e-12,
                ref pass, ref fail, "Для тонкостенной наружной трубы реальная малая толщина до внутренней стенки не маскируется радиусом.");
            Check("TAPER-WALL-INTERNAL-RAY-PRESERVED", Math.Abs(ThreadWallThicknessPolicy.EffectiveRadialWallThicknessCm(false, 0.35, 1.0) - 0.35) < 1e-12,
                ref pass, ref fail, "Для внутреннего отверстия луч сохраняет прямое измерение стенки до наружной поверхности.");
            Check("TAPER-WALL-EXTERNAL-RAY-USES-FIRST-MATERIAL-INTERVAL", ThreadWallThicknessPolicy.TryGetFirstDistinctIntersectionIntervalCm(
                new[] { 0.005, 0.0050000001, 0.355, 0.905 }, out double externalWallIntervalCm) &&
                Math.Abs(externalWallIntervalCm - 0.35) < 1e-9,
                ref pass, ref fail, "Для наружной стенки используется расстояние между внешней поверхностью и ближайшей внутренней границей, а совпавшие реберные попадания сливаются.");
            Check("TAPER-WALL-EXTERNAL-OUTSIDE-RAY-REJECTS-ONE-HIT", !ThreadWallThicknessPolicy.TryGetFirstDistinctIntersectionIntervalCm(
                new[] { 0.005, 0.0050000001 }, out double externalMissingWallIntervalCm),
                ref pass, ref fail, "При старте луча снаружи одной точки недостаточно для определения интервала материала.");
            Check("TAPER-WALL-EXTERNAL-SOLID-SHAFT-SINGLE-HIT-ALLOWED", ThreadWallThicknessPolicy.TryGetExternalRadialWallThicknessCm(
                new[] { 5.1995 }, 0.0005, 2.6, out double externalSolidShaftWallCm) && Math.Abs(externalSolidShaftWallCm - 2.6) < 1e-9,
                ref pass, ref fail, "Луч, начатый внутри сплошного вала, может дать одну противоположную границу; оценка ограничивается радиусом, а не полным диаметром.");
            Check("TAPER-WALL-EXTERNAL-THIN-TUBE-SINGLE-HIT-PRESERVED", ThreadWallThicknessPolicy.TryGetExternalRadialWallThicknessCm(
                new[] { 0.1795, 5.0 }, 0.0005, 2.6, out double externalThinTubeWallCm) && Math.Abs(externalThinTubeWallCm - 0.18) < 1e-9,
                ref pass, ref fail, "Первое пересечение изнутри сохраняет фактическую тонкую стенку трубы и не подменяет её радиусом заготовки.");
            Check("TAPER-WALL-EXTERNAL-INSIDE-RAY-REJECTS-NO-HITS", !ThreadWallThicknessPolicy.TryGetExternalRadialWallThicknessCm(
                Array.Empty<double>(), 0.0005, 2.6, out double externalNoHitWallCm),
                ref pass, ref fail, "Если луч, начатый внутри материала, не даёт пересечений, проверка по-прежнему закрывается безопасным отказом.");
            Check("TAPER-DIAMETER-MISMATCH-DETECT", !TaperedThreadGeometryPolicy.IsStartingDiameterCompatible(22.0, 7.938, out double taperToleranceReject),
                ref pass, ref fail, "Сильно различающийся диаметр отмечается как требующий предупреждения; UI получает согласие пользователя вместо молчаливой подмены.");
            Check("TAPER-DIAMETER-NPT-HALF-NEAR", TaperedThreadGeometryPolicy.IsStartingDiameterCompatible(22.0, 21.336, out double taperToleranceMatch),
                ref pass, ref fail, "Диаметр Ø22 близок к типоразмеру около 1/2 NPT, но выбор всё равно подтверждается предупреждением изменения геометрии.");
            Check("TAPER-CLOSEST-DESIGNATION-22MM", TaperedThreadGeometryPolicy.FindClosestDesignation(22.0,
                new[] { Tuple.Create("1/16", 7.938), Tuple.Create("1/4", 13.716), Tuple.Create("3/8", 17.145), Tuple.Create("1/2", 21.336), Tuple.Create("3/4", 26.67), Tuple.Create("1", 33.401) }) == "1/2",
                ref pass, ref fail, "Для цилиндра Ø22 мм из предложенных таблицей NPT размеров выбирается ближайший большой подходящий типоразмер, а не первая строка 1/16.");
            Check("TAPER-CLOSEST-DESIGNATION-INVALID-REJECT", TaperedThreadGeometryPolicy.FindClosestDesignation(double.NaN,
                new[] { Tuple.Create("1/2", 19.885) }) == null &&
                TaperedThreadGeometryPolicy.FindClosestDesignation(22.0, Array.Empty<Tuple<string, double>>()) == null,
                ref pass, ref fail, "Подбор типа-размера отклоняет недопустимый диаметр и пустой список кандидатов.");
            Check("TAPER-AUTO-DESIGNATION-REJECTS-REMOTE-JIS-R6", TaperedThreadGeometryPolicy.FindClosestDesignation(350.0,
                new[] { Tuple.Create("R6", 165.2) }) == null,
                ref pass, ref fail, "JIS R6 не предлагается для Ø350 мм, если цилиндр больше максимального диаметра активной таблицы.");
            Check("TAPER-AUTO-DESIGNATION-REJECTS-REMOTE-NPT-12", TaperedThreadGeometryPolicy.FindClosestDesignation(350.0,
                new[] { Tuple.Create("10 NPT", 273.05), Tuple.Create("12 NPT", 323.85) }) == null,
                ref pass, ref fail, "12 NPT не предлагается для Ø350 мм, если цилиндр больше максимального наружного диаметра NPT в таблице.");
            Check("TAPER-AUTO-DESIGNATION-NEAREST-JIS-45MM", TaperedThreadGeometryPolicy.FindClosestDesignation(45.0,
                new[] { Tuple.Create("R 1 1/4", 42.7), Tuple.Create("R 1 1/2", 48.6) }) == "R 1 1/4",
                ref pass, ref fail, "Для Ø45 мм из доступных JIS Taper размеров выбирается ближайший R 1 1/4 с последующим подтверждением пользователя.");
            Check("TAPER-AUTO-DESIGNATION-NEAREST-NPT-45MM", TaperedThreadGeometryPolicy.FindClosestDesignation(45.0,
                new[] { Tuple.Create("1 1/4 NPT", 42.164), Tuple.Create("1 1/2 NPT", 48.26) }) == "1 1/4 NPT",
                ref pass, ref fail, "Для Ø45 мм из доступных NPT размеров выбирается ближайший табличный номинал с последующим подтверждением пользователя.");

            double hostLengthCm;
            string taperLengthError;
            Check("TAPER-HOST-LENGTH-FULL", TaperedThreadGeometryPolicy.TryGetHostLengthCm(true, 4.0, 12.0, 5.0,
                out hostLengthCm, out taperLengthError) && Math.Abs(hostLengthCm - 4.0) < 1e-12,
                ref pass, ref fail, "Полный режим готовит конус от исходного ребра до дальнего торца.");
            Check("TAPER-HOST-LENGTH-LIMITED-OFFSET-ONCE", TaperedThreadGeometryPolicy.TryGetHostLengthCm(false, 4.0, 12.0, 5.0,
                out hostLengthCm, out taperLengthError) && Math.Abs(hostLengthCm - 1.7) < 1e-12,
                ref pass, ref fail, "Ограниченный конус включает отступ и длину резьбы ровно один раз.");
            Check("TAPER-HOST-LENGTH-OVERFLOW-REJECT", !TaperedThreadGeometryPolicy.TryGetHostLengthCm(false, 2.0, 13.0, 8.0,
                out hostLengthCm, out taperLengthError), ref pass, ref fail,
                "Длина конического участка за пределами исходной поверхности отклоняется.");

            bool typedHostInvalidInputs = !TaperedThreadGeometryPolicy.TryGetHostLengthFailure(false, 2.0, double.NaN, 0.0,
                out double typedHostLengthA, out HelivantaPolicyFailure typedHostFailureA) &&
                typedHostFailureA != null && typedHostFailureA.Code == "TaperedHostLength.InputsInvalid" &&
                typedHostFailureA.ResourceKey == "TaperedThread.Validation.HostLengthInputsInvalid" && typedHostLengthA == 0.0;
            bool typedHostOffsetInvalid = !TaperedThreadGeometryPolicy.TryGetHostLengthFailure(true, 1.0, 5.0, 10.0,
                out double typedHostLengthB, out HelivantaPolicyFailure typedHostFailureB) &&
                typedHostFailureB != null && typedHostFailureB.Code == "TaperedHostLength.OffsetNotShorterThanSurface" &&
                typedHostFailureB.ResourceKey == "TaperedThread.Validation.OffsetMustBeShorterThanSurface" && typedHostLengthB == 0.0;
            bool typedHostLengthInvalid = !TaperedThreadGeometryPolicy.TryGetHostLengthFailure(false, 2.0, 0.0, 0.0,
                out double typedHostLengthC, out HelivantaPolicyFailure typedHostFailureC) &&
                typedHostFailureC != null && typedHostFailureC.Code == "TaperedHostLength.LimitedLengthInvalid" &&
                typedHostFailureC.ResourceKey == "TaperedThread.Validation.LimitedLengthMustBePositive" && typedHostLengthC == 0.0;
            bool typedHostOutside = !TaperedThreadGeometryPolicy.TryGetHostLengthFailure(false, 2.0, 20.0, 5.0,
                out double typedHostLengthD, out HelivantaPolicyFailure typedHostFailureD) &&
                typedHostFailureD != null && typedHostFailureD.Code == "TaperedHostLength.OutsideSurface" &&
                typedHostFailureD.ResourceKey == "TaperedThread.Validation.HostIntervalOutOfRange" && typedHostLengthD == 0.0;
            Check("TAPER-HOST-LENGTH-FAILURES-USE-STABLE-CODES-AND-RESOURCE-KEYS",
                typedHostInvalidInputs && typedHostOffsetInvalid && typedHostLengthInvalid && typedHostOutside,
                ref pass, ref fail, "All four axial-length rejection branches expose stable typed failures while preserving fail-closed output length.");
            string[] taperedLengthFailureKeys = { "TaperedThread.Validation.HostLengthInputsInvalid",
                "TaperedThread.Validation.OffsetMustBeShorterThanSurface", "TaperedThread.Validation.LimitedLengthMustBePositive",
                "TaperedThread.Validation.HostIntervalOutOfRange" };
            Check("TAPER-HOST-LENGTH-FAILURE-RESOURCES-HAVE-RU-EN-PARITY",
                russianCatalog != null && englishCatalog != null && taperedLengthFailureKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !englishCatalog.GetString(key, CultureInfo.InvariantCulture).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))),
                ref pass, ref fail, "Tapered host-length error reasons are explicit in both catalogs and English never falls back to Cyrillic.");

            Check("TAPER-END-RADIUS-CALC", TaperedThreadGeometryPolicy.TryGetEndRadiusCm(1.0, 3.2, 1.0 / 16.0,
                out hostLengthCm) && Math.Abs(hostLengthCm - 0.9) < 1e-12,
                ref pass, ref fail, "Изменение радиуса равно половине изменения диаметра при конусности 1:16.");
            Check("TAPER-END-RADIUS-INVALID-REJECT", !TaperedThreadGeometryPolicy.TryGetEndRadiusCm(0.01, 0.32, 1.0 / 16.0,
                out hostLengthCm), ref pass, ref fail, "Нулевой конечный радиус отклоняется до изменения модели.");
            Check("TAPER-EXTERNAL-DIAMETER-GROWS-TO-SHOULDER",
                TaperedThreadGeometryPolicy.TryGetEndRadiusCm(0.9668, 3.2, 1.0 / 16.0, true, out double externalEndRadius) &&
                Math.Abs(externalEndRadius - 1.0668) < 1e-9,
                ref pass, ref fail, "Наружная резьба от свободного малого торца увеличивается к плечу.");
            Check("TAPER-INTERNAL-DIAMETER-SHRINKS-INWARD",
                TaperedThreadGeometryPolicy.TryGetEndRadiusCm(1.0, 3.2, 1.0 / 16.0, false, out double internalEndRadius) &&
                Math.Abs(internalEndRadius - 0.9) < 1e-9,
                ref pass, ref fail, "Внутренняя резьба от широкой горловины сужается внутрь.");
            double standardStartRadius;
            Check("TAPER-START-RADIUS-EXTERNAL-SMALL-END", TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(true, true,
                21.336, 19.885, 0.0, 3.2, 0.0, 1.0 / 16.0, out standardStartRadius) &&
                Math.Abs(standardStartRadius - 0.9668) < 1e-9,
                ref pass, ref fail, "Наружная резьба использует OutsidePipeDiameter, затем растёт от малого конца к плечу.");
            Check("TAPER-START-RADIUS-EXTERNAL-FALLBACK", TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(true, true,
                double.NaN, 20.0, 0.0, 3.2, 0.0, 1.0 / 16.0, out standardStartRadius) &&
                Math.Abs(standardStartRadius - 0.9) < 1e-9,
                ref pass, ref fail, "NominalExternalDiameter используется только как fallback при отсутствии OutsidePipeDiameter.");
            Check("TAPER-START-RADIUS-EXTERNAL-INCH", TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(true, false,
                0.840, 0.783, 0.0, 3.2, 0.0, 1.0 / 16.0, out standardStartRadius) &&
                Math.Abs(standardStartRadius - 0.9668) < 1e-9,
                ref pass, ref fail, "Дюймовой OutsidePipeDiameter переводится в мм до построения малого конца.");
            Check("TAPER-START-RADIUS-EXTERNAL-OFFSET-INCLUDED-ONCE", TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(true, true,
                21.336, 19.885, 0.0, 3.2, 2.0, 1.0 / 16.0, out standardStartRadius) &&
                Math.Abs(standardStartRadius - 0.9668) < 1e-9,
                ref pass, ref fail, "Отступ включён в длину конуса ровно один раз.");
            Check("TAPER-START-RADIUS-INTERNAL-LARGE-END", TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(false, true,
                0.0, 0.0, 10.0, 3.2, 0.0, 1.0 / 16.0, out standardStartRadius) &&
                Math.Abs(standardStartRadius - 0.6) < 1e-9,
                ref pass, ref fail, "Для внутреннего режима табличный малый конец увеличивается на конусность по длине.");
            Check("TAPER-START-RADIUS-REJECT-MISSING-INTERNAL", !TaperedThreadGeometryPolicy.TryGetStandardStartRadiusCm(false, true,
                0.0, 0.0, double.NaN, 3.2, 0.0, 1.0 / 16.0, out standardStartRadius),
                ref pass, ref fail, "Неизвестный внутренний табличный диаметр отклоняется, а не угадывается.");
            Check("TAPER-FREE-EXTERNAL-PRESERVES-FAR-END", TaperedThreadGeometryPolicy.TryGetStartRadiusFromEndCm(
                true, 2.6, 3.0, 1.0 / 16.0, out double freeExternalStart) &&
                Math.Abs(freeExternalStart - 2.50625) < 1e-12,
                ref pass, ref fail, "Свободный наружный режим сохраняет дальний диаметр и уменьшает входной радиус на L/32.");
            Check("TAPER-FREE-INTERNAL-PRESERVES-FAR-END", TaperedThreadGeometryPolicy.TryGetStartRadiusFromEndCm(
                false, 2.6, 3.0, 1.0 / 16.0, out double freeInternalStart) &&
                Math.Abs(freeInternalStart - 2.69375) < 1e-12,
                ref pass, ref fail, "Свободный внутренний режим сохраняет дальний диаметр и увеличивает входной радиус на L/32.");
            Check("TAPER-FREE-INVALID-RADIUS-REJECT", !TaperedThreadGeometryPolicy.TryGetStartRadiusFromEndCm(
                true, 0.0, 3.0, 1.0 / 16.0, out double freeInvalidStart),
                ref pass, ref fail, "Свободный режим не принимает нулевой исходный радиус.");
            bool joinOperation;
            string taperOperationError;
            Check("TAPER-EXTERNAL-CUT", TaperedThreadGeometryPolicy.TryChooseJoinOperation(true, 1.0, 1.0, 0.9,
                out joinOperation, out taperOperationError) && !joinOperation,
                ref pass, ref fail, "Наружный табличный профиль вырезается из заготовки совпадающего старта.");
            Check("TAPER-INTERNAL-JOIN", TaperedThreadGeometryPolicy.TryChooseJoinOperation(false, 1.0, 1.0, 0.9,
                out joinOperation, out taperOperationError) && joinOperation,
                ref pass, ref fail, "Внутренний профиль добавляет материал и сужает отверстие по конусу.");
            Check("TAPER-OUTER-UNDERSIZED-STOCK-JOIN", TaperedThreadGeometryPolicy.TryChooseJoinOperation(true, 0.8, 1.0, 0.9,
                out joinOperation, out taperOperationError) && joinOperation,
                ref pass, ref fail, "Недоразмерный наружный цилиндр можно нарастить до стандартного конуса.");
            Check("TAPER-INNER-UNDERSIZED-BORE-CUT", TaperedThreadGeometryPolicy.TryChooseJoinOperation(false, 0.8, 1.0, 0.9,
                out joinOperation, out taperOperationError) && !joinOperation,
                ref pass, ref fail, "Малое внутреннее отверстие можно расточить до стандартного конуса.");
            Check("TAPER-SINGLE-OPERATION-CROSSING-REQUIRES-SPLIT", !TaperedThreadGeometryPolicy.TryChooseJoinOperation(true, 0.95, 1.0, 0.9,
                out joinOperation, out taperOperationError),
                ref pass, ref fail, "Один булев Cut/Join не может обработать пересечение; runtime должен разбить конус на два участка.");
            Check("TAPER-EXTERNAL-CROSSING-STATION", TaperedThreadGeometryPolicy.TryGetStockCrossingStationCm(1.55, 1.4567, 1.5817, 4.0,
                out bool externalCrosses, out double externalCrossing, out string externalCrossingError) && externalCrosses &&
                Math.Abs(externalCrossing - 2.9856) < 1e-3 &&
                !TaperedThreadGeometryPolicy.RequiresJoinForRadialChange(true, 1.55, 1.4567) &&
                TaperedThreadGeometryPolicy.RequiresJoinForRadialChange(true, 1.55, 1.5817),
                ref pass, ref fail, "Наружный конус, пересекающий Ø31 заготовку, делится на Cut до пересечения и Join после него.");
            Check("TAPER-INTERNAL-CROSSING-STATION", TaperedThreadGeometryPolicy.TryGetStockCrossingStationCm(2.25, 2.2864, 2.19265, 4.0,
                out bool internalCrosses, out double internalCrossing, out string internalCrossingError) && internalCrosses &&
                Math.Abs(internalCrossing - (0.0364 * 4.0 / (2.2864 - 2.19265))) < 1e-6 &&
                !TaperedThreadGeometryPolicy.RequiresJoinForRadialChange(false, 2.25, 2.2864) &&
                TaperedThreadGeometryPolicy.RequiresJoinForRadialChange(false, 2.25, 2.19265),
                ref pass, ref fail, "Внутренний конус, пересекающий Ø45 отверстие, делится на Cut для входа и Join для сужения.");
            Check("TAPER-NON-CROSSING-STAYS-SINGLE-OP", TaperedThreadGeometryPolicy.TryGetStockCrossingStationCm(1.2, 1.0, 0.9, 4.0,
                out bool noCross, out double noCrossStation, out string noCrossError) && !noCross,
                ref pass, ref fail, "Если заготовка целиком толще или тоньше конуса, остаётся одна булева операция.");
            Check("TAPER-EXTERNAL-INCREASING-CONE-CUT", TaperedThreadGeometryPolicy.TryChooseJoinOperation(true, 1.1, 0.9, 1.0,
                out joinOperation, out taperOperationError) && !joinOperation,
                ref pass, ref fail, "Для растущего наружного конуса более крупная заготовка обрабатывается вырезанием.");
            Check("TAPER-INTERNAL-DECREASING-CONE-JOIN", TaperedThreadGeometryPolicy.TryChooseJoinOperation(false, 1.1, 1.0, 0.9,
                out joinOperation, out taperOperationError) && joinOperation,
                ref pass, ref fail, "Для сужающегося внутреннего отверстия larger stock radius приводит к добавлению материала.");
            Check("TAPER-INTERNAL-INCREASING-BULK-CUT", TaperedThreadGeometryPolicy.TryChooseJoinOperation(false, 0.8, 0.9, 1.0,
                out joinOperation, out taperOperationError) && !joinOperation,
                ref pass, ref fail, "Выбор булевой операции не зависит от того, растёт или уменьшается конус.");

            ThreadMetadataRecord original = new ThreadMetadataRecord
            {
                SchemaVersion = 1,
                ThreadId = "0123456789abcdef0123456789abcdef",
                ProducerVersion = "2.0.0-preview.15-alpha3",
                ObjectCount = 17,
                Parameters = new ThreadParametersSnapshot
                {
                    Standard = 3,
                    SizeName = "Tr20×4",
                    PitchMm = 4.0,
                    NominalDiaMm = 20.0,
                    Starts = 2,
                    ClearanceMm = 0.2,
                    External = true,
                    Round = true,
                    Smooth = true,
                    SimplifiedRunout = false,
                    RunSteps = 3.5,
                    Mirror = false,
                    SortThreadsIntoFolders = true,
                    FullLength = false,
                    LengthMm = 30.0,
                    OffsetMm = 1.25,
                    RunOut = false,
                    RightHand = true,
                    ChamferMode = 3,
                    ChamferMm = 0.75,
                    TaperThreadDepthMm = 0.75284,
                    TaperGeometryMode = 1,
                    TaperDisplayDesignation = "1/2 - 14 NPT x 18.796mm"
                },
                Target = new ThreadTargetSnapshot
                {
                    Center = new[] { 10.25, -4.5, 8.0 },
                    Axis = new[] { 0.0, 0.0, -1.0 },
                    RadiusCm = 1.0,
                    LengthCm = 6.2,
                    IsExternal = true,
                    BodyName = "Solid1",
                    StartIsShoulder = true,
                    EndIsShoulder = false
                }
            };

            Check("METADATA-VALID-RECORD", original.IsValid(), ref pass, ref fail,
                "Полная запись метаданных проходит проверку схемы и геометрического снимка.");

            string json = JsonSerializer.Serialize(original);
            ThreadMetadataRecord restored = JsonSerializer.Deserialize<ThreadMetadataRecord>(json);
            Check("METADATA-JSON-ROUNDTRIP", restored != null && restored.IsValid(), ref pass, ref fail,
                "JSON-запись можно восстановить и повторно проверить.");
            Check("METADATA-PARAMETERS-ROUNDTRIP", restored != null && restored.Parameters != null &&
                restored.Parameters.Standard == original.Parameters.Standard &&
                restored.Parameters.SizeName == original.Parameters.SizeName &&
                restored.Parameters.PitchMm == original.Parameters.PitchMm &&
                restored.Parameters.NominalDiaMm == original.Parameters.NominalDiaMm &&
                restored.Parameters.TaperThreadDepthMm == original.Parameters.TaperThreadDepthMm &&
                restored.Parameters.TaperDisplayDesignation == original.Parameters.TaperDisplayDesignation &&
                restored.Parameters.TaperNativeStandard == original.Parameters.TaperNativeStandard &&
                restored.Parameters.TaperNativeDesignation == original.Parameters.TaperNativeDesignation &&
                restored.Parameters.TaperGeometryMode == original.Parameters.TaperGeometryMode &&
                restored.Parameters.Starts == original.Parameters.Starts &&
                restored.Parameters.ClearanceMm == original.Parameters.ClearanceMm &&
                restored.Parameters.External == original.Parameters.External &&
                restored.Parameters.Round == original.Parameters.Round &&
                restored.Parameters.Smooth == original.Parameters.Smooth &&
                restored.Parameters.SimplifiedRunout == original.Parameters.SimplifiedRunout &&
                restored.Parameters.RunSteps == original.Parameters.RunSteps &&
                restored.Parameters.Mirror == original.Parameters.Mirror &&
                restored.Parameters.SortThreadsIntoFolders == original.Parameters.SortThreadsIntoFolders &&
                restored.Parameters.FullLength == original.Parameters.FullLength &&
                restored.Parameters.LengthMm == original.Parameters.LengthMm &&
                restored.Parameters.OffsetMm == original.Parameters.OffsetMm &&
                restored.Parameters.RunOut == original.Parameters.RunOut &&
                restored.Parameters.RightHand == original.Parameters.RightHand &&
                restored.Parameters.ChamferMode == original.Parameters.ChamferMode &&
                restored.Parameters.ChamferMm == original.Parameters.ChamferMm,
                ref pass, ref fail, "Все поля параметров сохраняются без изменения.");
            Check("METADATA-TARGET-ROUNDTRIP", restored != null && restored.Target != null &&
                restored.Target.Center != null && restored.Target.Center.Length == 3 &&
                restored.Target.Center[0] == original.Target.Center[0] &&
                restored.Target.Center[1] == original.Target.Center[1] &&
                restored.Target.Center[2] == original.Target.Center[2] &&
                restored.Target.Axis != null && restored.Target.Axis.Length == 3 &&
                restored.Target.Axis[0] == original.Target.Axis[0] &&
                restored.Target.Axis[1] == original.Target.Axis[1] &&
                restored.Target.Axis[2] == original.Target.Axis[2] &&
                restored.Target.RadiusCm == original.Target.RadiusCm &&
                restored.Target.LengthCm == original.Target.LengthCm &&
                restored.Target.IsExternal == original.Target.IsExternal &&
                restored.Target.BodyName == original.Target.BodyName &&
                restored.Target.StartIsShoulder == original.Target.StartIsShoulder &&
                restored.Target.EndIsShoulder == original.Target.EndIsShoulder,
                ref pass, ref fail, "Геометрический снимок восстанавливается с исходными единицами и направлением оси.");

            ThreadMetadataRecord incomplete = new ThreadMetadataRecord
            {
                ThreadId = "incomplete",
                ObjectCount = 1,
                Parameters = original.Parameters,
                Target = new ThreadTargetSnapshot
                {
                    Center = new[] { 0.0, 0.0, 0.0 },
                    Axis = new[] { 0.0, 0.0, 2.0 },
                    RadiusCm = 1.0,
                    LengthCm = 1.0,
                    IsExternal = true,
                    BodyName = "Solid1"
                }
            };
            Check("METADATA-REJECT-NONUNIT-AXIS", !incomplete.IsValid(), ref pass, ref fail,
                "Повреждённая или не нормализованная ось не принимается.");

            ThreadMetadataRecord gostMetricRecord = CloneRecord(original);
            gostMetricRecord.Parameters.Standard = (int)ThreadStandard.GostMetric;
            gostMetricRecord.Parameters.SizeName = "M10 x 1.5";
            Check("METADATA-ACCEPT-GOST-METRIC-STANDARD-ID", gostMetricRecord.IsValid(), ref pass, ref fail,
                "Новый ID ГОСТ 6 допустим в схеме метаданных; старые ID остаются неизменными.");
            ThreadMetadataRecord cylindricalAfterTaperModeSwitch = CloneRecord(original);
            cylindricalAfterTaperModeSwitch.Parameters.Standard = (int)ThreadStandard.GostMetric;
            cylindricalAfterTaperModeSwitch.Parameters.SizeName = "M22 x 1.5";
            cylindricalAfterTaperModeSwitch.Parameters.PitchMm = 1.5;
            cylindricalAfterTaperModeSwitch.Parameters.NominalDiaMm = 22.0;
            cylindricalAfterTaperModeSwitch.Parameters.Tapered = false;
            cylindricalAfterTaperModeSwitch.Parameters.TaperStandard = "GOST 6211-81";
            cylindricalAfterTaperModeSwitch.Parameters.TaperDesignation = "R 1/2";
            Check("METADATA-REJECT-CYLINDRICAL-M22-WITH-STALE-GOST-TAPER-STANDARD",
                !cylindricalAfterTaperModeSwitch.IsValid(), ref pass, ref fail,
                "A cylindrical M22 record must still reject stale GOST 6211 tapered metadata left in a hidden UI selector.");
            cylindricalAfterTaperModeSwitch.Parameters.TaperStandard = string.Empty;
            cylindricalAfterTaperModeSwitch.Parameters.TaperDesignation = string.Empty;
            cylindricalAfterTaperModeSwitch.Parameters.TaperDisplayDesignation = string.Empty;
            cylindricalAfterTaperModeSwitch.Parameters.TaperNativeStandard = string.Empty;
            cylindricalAfterTaperModeSwitch.Parameters.TaperNativeDesignation = string.Empty;
            cylindricalAfterTaperModeSwitch.Parameters.TaperRatio = 0.0;
            cylindricalAfterTaperModeSwitch.Parameters.TaperReferenceDiameterMm = 0.0;
            cylindricalAfterTaperModeSwitch.Parameters.TaperReferenceLengthMm = 0.0;
            cylindricalAfterTaperModeSwitch.Parameters.TaperStartDiameterMm = 0.0;
            cylindricalAfterTaperModeSwitch.Parameters.TaperEndDiameterMm = 0.0;
            cylindricalAfterTaperModeSwitch.Parameters.TaperThreadDepthMm = 0.0;
            Check("METADATA-ACCEPT-CYLINDRICAL-M22-AFTER-TAPER-STATE-CLEAR",
                cylindricalAfterTaperModeSwitch.IsValid(), ref pass, ref fail,
                "After the UI clears inactive tapered fields, ordinary M22 metadata passes without weakening the GOST R/Rc guard.");
            ThreadMetadataRecord gostTrapezoidalRecord = CloneRecord(original);
            gostTrapezoidalRecord.Parameters.Standard = (int)ThreadStandard.GostTrapezoidal;
            gostTrapezoidalRecord.Parameters.SizeName = "Tr 32 x 3";
            Check("METADATA-ACCEPT-GOST-TRAPEZOIDAL-STANDARD-ID-7", gostTrapezoidalRecord.IsValid(), ref pass, ref fail,
                "Новый зарегистрированный ID ГОСТ Tr 7 принимается; прежние ID не изменяются.");

            ThreadMetadataRecord gostButtressRecord = CloneRecord(original);
            gostButtressRecord.Parameters.Standard = (int)ThreadStandard.GostButtress;
            gostButtressRecord.Parameters.SizeName = "S 30 x 3";
            Check("METADATA-ACCEPT-GOST-BUTTRESS-STANDARD-ID-8", gostButtressRecord.IsValid(), ref pass, ref fail,
                "Новый зарегистрированный ID ГОСТ S 8 принимается; прежние ID не изменяются.");

            ThreadMetadataRecord gostPipeExternalRecord = CloneRecord(original);
            gostPipeExternalRecord.Parameters.Standard = (int)ThreadStandard.GostPipeTapered;
            gostPipeExternalRecord.Parameters.SizeName = "R 1/2";
            gostPipeExternalRecord.Parameters.PitchMm = 1.814;
            gostPipeExternalRecord.Parameters.NominalDiaMm = 20.4425;
            gostPipeExternalRecord.Parameters.Starts = 1;
            gostPipeExternalRecord.Parameters.ToleranceClass = string.Empty;
            gostPipeExternalRecord.Parameters.Tapered = true;
            gostPipeExternalRecord.Parameters.TaperGeometryMode = (int)TaperGeometryModeKind.StrictStandard;
            gostPipeExternalRecord.Parameters.TaperStandard = "GOST 6211-81";
            gostPipeExternalRecord.Parameters.TaperDesignation = "R 1/2";
            gostPipeExternalRecord.Parameters.TaperDisplayDesignation = "R 1/2";
            gostPipeExternalRecord.Parameters.TaperNativeStandard = "JIS Taper";
            gostPipeExternalRecord.Parameters.TaperNativeDesignation = "1/2-14";
            gostPipeExternalRecord.Parameters.TaperRatio = 1.0 / 16.0;
            gostPipeExternalRecord.Parameters.TaperReferenceDiameterMm = 20.4425;
            gostPipeExternalRecord.Parameters.TaperReferenceLengthMm = 13.2;
            gostPipeExternalRecord.Parameters.TaperStartDiameterMm = 20.4425;
            gostPipeExternalRecord.Parameters.TaperEndDiameterMm = 21.2675;
            gostPipeExternalRecord.Parameters.TaperThreadDepthMm = 1.161548;
            Check("METADATA-ACCEPT-GOST-PIPE-R-ID-9", gostPipeExternalRecord.IsValid(), ref pass, ref fail,
                "ID 9 принимается только как коническая наружная R по ГОСТ 6211-81 с точной JIS Taper carrier-меткой и без класса допуска.");

            ThreadMetadataRecord gostPipeFreeGeometryRecord = CloneRecord(gostPipeExternalRecord);
            gostPipeFreeGeometryRecord.Parameters.TaperGeometryMode = (int)TaperGeometryModeKind.FreeGeometry;
            gostPipeFreeGeometryRecord.Parameters.NominalDiaMm = 21.175;
            gostPipeFreeGeometryRecord.Parameters.TaperReferenceDiameterMm = 21.175;
            gostPipeFreeGeometryRecord.Parameters.TaperStartDiameterMm = 21.175;
            gostPipeFreeGeometryRecord.Parameters.TaperEndDiameterMm = 22.0;
            gostPipeFreeGeometryRecord.Parameters.TaperReferenceLengthMm = 13.2;
            Check("METADATA-ACCEPT-GOST-PIPE-R-FREE-GEOMETRY-D22", gostPipeFreeGeometryRecord.IsValid(), ref pass, ref fail,
                "Свободный GOST R 1/2 сохраняет исходный Ø22 мм и точную конусность 1:16 на длине 13,2 мм.");

            ThreadMetadataRecord gostPipeUnknownGeometryMode = CloneRecord(gostPipeFreeGeometryRecord);
            gostPipeUnknownGeometryMode.Parameters.TaperGeometryMode = 2;
            Check("METADATA-REJECT-GOST-PIPE-UNKNOWN-GEOMETRY-MODE", !gostPipeUnknownGeometryMode.IsValid(), ref pass, ref fail,
                "Неизвестные режимы геометрии конической резьбы по-прежнему отклоняются.");

            ThreadMetadataRecord gostPipeInternalRecord = CloneRecord(gostPipeExternalRecord);
            gostPipeInternalRecord.Parameters.SizeName = "Rc 1/2";
            gostPipeInternalRecord.Parameters.NominalDiaMm = 19.456;
            gostPipeInternalRecord.Parameters.External = false;
            gostPipeInternalRecord.Target.IsExternal = false;
            gostPipeInternalRecord.Parameters.TaperDesignation = "Rc 1/2";
            gostPipeInternalRecord.Parameters.TaperDisplayDesignation = "Rc 1/2";
            gostPipeInternalRecord.Parameters.TaperReferenceDiameterMm = 19.456;
            gostPipeInternalRecord.Parameters.TaperStartDiameterMm = 19.456;
            gostPipeInternalRecord.Parameters.TaperEndDiameterMm = 18.631;
            Check("METADATA-ACCEPT-GOST-PIPE-RC-ID-9", gostPipeInternalRecord.IsValid(), ref pass, ref fail,
                "ID 9 принимает также внутреннюю Rc с отдельной геометрической ориентацией и тем же нормативным рядом.");

            ThreadMetadataRecord gostPipeWrongTaperLength = CloneRecord(gostPipeExternalRecord);
            gostPipeWrongTaperLength.Parameters.TaperReferenceLengthMm = 0.0;
            Check("METADATA-REJECT-GOST-PIPE-MISSING-CONE-LENGTH", !gostPipeWrongTaperLength.IsValid(), ref pass, ref fail,
                "Метаданные R/Rc без положительной осевой длины конуса отклоняются.");

            ThreadMetadataRecord gostPipeWrongTaperRatio = CloneRecord(gostPipeExternalRecord);
            gostPipeWrongTaperRatio.Parameters.TaperEndDiameterMm += 0.2;
            Check("METADATA-REJECT-GOST-PIPE-WRONG-AXIAL-TAPER", !gostPipeWrongTaperRatio.IsValid(), ref pass, ref fail,
                "Сохранённые начальный/конечный диаметры обязаны описывать точную конусность 1:16.");

            ThreadMetadataRecord gostPipeRejectRp = CloneRecord(gostPipeExternalRecord);
            gostPipeRejectRp.Parameters.SizeName = "Rp 1/2";
            gostPipeRejectRp.Parameters.TaperDesignation = "Rp 1/2";
            gostPipeRejectRp.Parameters.TaperDisplayDesignation = "Rp 1/2";
            Check("METADATA-REJECT-GOST-PIPE-RP", !gostPipeRejectRp.IsValid(), ref pass, ref fail,
                "Цилиндрическая внутренняя Rp не входит в ГОСТ 6211-81 R/Rc.");

            ThreadMetadataRecord gostPipeRejectG = CloneRecord(gostPipeExternalRecord);
            gostPipeRejectG.Parameters.SizeName = "G 1/2";
            gostPipeRejectG.Parameters.TaperDesignation = "G 1/2";
            gostPipeRejectG.Parameters.TaperDisplayDesignation = "G 1/2";
            Check("METADATA-REJECT-GOST-PIPE-G", !gostPipeRejectG.IsValid(), ref pass, ref fail,
                "Цилиндрическая наружная G по ГОСТ 6357-81 не входит в ГОСТ 6211-81 и не поддерживается Helivanta.");

            ThreadMetadataRecord gostPipeRejectWrongSex = CloneRecord(gostPipeExternalRecord);
            gostPipeRejectWrongSex.Parameters.External = false;
            gostPipeRejectWrongSex.Target.IsExternal = false;
            Check("METADATA-REJECT-GOST-PIPE-R-DESIGNATION-ON-INTERNAL", !gostPipeRejectWrongSex.IsValid(), ref pass, ref fail,
                "Наружное обозначение R нельзя сохранить как внутреннюю резьбу; внутренний тип требует Rc.");

            ThreadMetadataRecord gostPipeRejectRcOnExternal = CloneRecord(gostPipeInternalRecord);
            gostPipeRejectRcOnExternal.Parameters.External = true;
            gostPipeRejectRcOnExternal.Target.IsExternal = true;
            Check("METADATA-REJECT-GOST-PIPE-RC-DESIGNATION-ON-EXTERNAL", !gostPipeRejectRcOnExternal.IsValid(), ref pass, ref fail,
                "Внутреннее обозначение Rc нельзя сохранить как наружную резьбу; наружный тип требует R.");

            ThreadMetadataRecord gostPipeWrongCarrier = CloneRecord(gostPipeExternalRecord);
            gostPipeWrongCarrier.Parameters.TaperNativeStandard = "NPT";
            Check("METADATA-REJECT-GOST-PIPE-NPT-CARRIER", !gostPipeWrongCarrier.IsValid(), ref pass, ref fail,
                "Для GOST R/Rc нельзя использовать NPT carrier вместо точной BSPT/JIS Taper записи.");

            ThreadMetadataRecord unknownStandardRecord = CloneRecord(original);
            unknownStandardRecord.Parameters.Standard = 10;
            Check("METADATA-REJECT-UNKNOWN-STANDARD-ID-10", !unknownStandardRecord.IsValid(), ref pass, ref fail,
                "Принимаются только ID, явно объявленные в ThreadStandard; произвольные значения по-прежнему отклоняются.");
            ThreadMetadataRecord negativeStandardRecord = CloneRecord(original);
            negativeStandardRecord.Parameters.Standard = -1;
            Check("METADATA-REJECT-NEGATIVE-STANDARD-ID", !negativeStandardRecord.IsValid(), ref pass, ref fail,
                "Отрицательные и не зарегистрированные ID стандартов отклоняются.");

            ThreadMetadataRecord mismatchedType = new ThreadMetadataRecord
            {
                SchemaVersion = 1,
                ThreadId = "mismatch",
                ProducerVersion = "test",
                ObjectCount = 1,
                Parameters = new ThreadParametersSnapshot { Standard = 0, SizeName = "M10×1,5", PitchMm = 1.5, NominalDiaMm = 10.0, Starts = 1, External = false },
                Target = new ThreadTargetSnapshot { Center = new[] { 0.0, 0.0, 0.0 }, Axis = new[] { 0.0, 0.0, 1.0 }, RadiusCm = 0.5, LengthCm = 2.0, IsExternal = true, BodyName = "Solid1" }
            };
            Check("METADATA-REJECT-TYPE-MISMATCH", !mismatchedType.IsValid(), ref pass, ref fail,
                "Тип параметров не может расходиться с типом сохранённой цилиндрической поверхности.");

            ThreadMetadataRecord offsetAtEnd = CloneRecord(original);
            offsetAtEnd.Parameters.FullLength = true;
            offsetAtEnd.Parameters.OffsetMm = offsetAtEnd.Target.LengthCm * 10.0;
            Check("METADATA-REJECT-OFFSET-AT-CYLINDER-END", !offsetAtEnd.IsValid(), ref pass, ref fail,
                "Отступ, достигающий конца исходного цилиндра, не допускается даже в режиме полной длины.");

            ThreadMetadataRecord zeroLimitedLength = CloneRecord(original);
            zeroLimitedLength.Parameters.FullLength = false;
            zeroLimitedLength.Parameters.LengthMm = 0.0;
            Check("METADATA-REJECT-ZERO-LIMITED-LENGTH", !zeroLimitedLength.IsValid(), ref pass, ref fail,
                "Ограниченная резьба с нулевой длиной не может считаться безопасно редактируемой.");

            ThreadMetadataRecord overflowingLength = CloneRecord(original);
            overflowingLength.Parameters.FullLength = false;
            overflowingLength.Parameters.LengthMm = overflowingLength.Target.LengthCm * 10.0 -
                overflowingLength.Parameters.OffsetMm + 0.01;
            Check("METADATA-REJECT-LIMITED-LENGTH-OVERFLOW", !overflowingLength.IsValid(), ref pass, ref fail,
                "Сохранённая ограниченная длина вместе с отступом не должна выходить за исходный цилиндр.");

            ThreadMetadataRecord boundaryLength = CloneRecord(original);
            boundaryLength.Parameters.FullLength = false;
            boundaryLength.Parameters.LengthMm = boundaryLength.Target.LengthCm * 10.0 -
                boundaryLength.Parameters.OffsetMm;
            Check("METADATA-ACCEPT-LIMITED-LENGTH-AT-BOUNDARY", boundaryLength.IsValid(), ref pass, ref fail,
                "Ограниченная резьба, точно заканчивающаяся на границе цилиндра, остаётся допустимой.");

            ThreadMetadataRecord fullLengthWithStaleManualLength = CloneRecord(original);
            fullLengthWithStaleManualLength.Parameters.FullLength = true;
            fullLengthWithStaleManualLength.Parameters.LengthMm = 0.0;
            Check("METADATA-FULL-LENGTH-IGNORES-MANUAL-LENGTH", fullLengthWithStaleManualLength.IsValid(), ref pass, ref fail,
                "В режиме полной длины сохранённое неактивное поле ограниченной длины не блокирует запись.");

            ThreadMetadataRecord capNeckRecord = new ThreadMetadataRecord
            {
                SchemaVersion = 2,
                Family = ThreadMetadataRecord.CapNeckFamily,
                ThreadId = "capneck-edit-test-0123456789abcdef",
                ProducerVersion = "test",
                ObjectCount = 12,
                PackagingParameters = new PackagingThreadParametersSnapshot
                {
                    DiameterMm = 22.0,
                    PitchMm = 2.7,
                    Starts = 2,
                    HeightRatio = 0.05,
                    ClearanceMm = 0.2,
                    RightHand = true,
                    InternalThread = false,
                    LengthInputValue = 22.0,
                    LengthAsTurns = false,
                    ResolvedAxialLengthMm = 22.0,
                    ResolvedStartOffsetMm = 1.25,
                    MeasuredWallMm = 4.0,
                    AutoPitchEnabled = false,
                    SmoothStart = true,
                    SmoothEnd = true,
                    VentSlotsEnabled = true,
                    VentSlotCount = 3,
                    VentSlotWidthDegrees = 14.0,
                    ChamferMode = 1,
                    ChamferMm = 0.3
                },
                Target = new ThreadTargetSnapshot
                {
                    Center = new[] { 10.25, -4.5, 8.0 },
                    Axis = new[] { 0.0, 0.0, -1.0 },
                    RadiusCm = 1.1,
                    LengthCm = 6.2,
                    IsExternal = true,
                    BodyName = "Solid1",
                    StartIsShoulder = true,
                    EndIsShoulder = false
                }
            };
            Check("CAPNECK-METADATA-V2-VALID", capNeckRecord.IsPackagingThread && capNeckRecord.IsValid() && capNeckRecord.Parameters == null,
                ref pass, ref fail, "Семейство крышек/горловин хранит схему 2 и собственный полный снимок параметров, не смешанный с ThreadParameters.");
            ThreadMetadataRecord capNeckRoundTrip = JsonSerializer.Deserialize<ThreadMetadataRecord>(JsonSerializer.Serialize(capNeckRecord));
            Check("CAPNECK-METADATA-V2-ROUNDTRIP", capNeckRoundTrip != null && capNeckRoundTrip.IsValid() &&
                capNeckRoundTrip.PackagingParameters != null && capNeckRoundTrip.PackagingParameters.PitchMm == 2.7 &&
                capNeckRoundTrip.PackagingParameters.HeightRatio == 0.05 && capNeckRoundTrip.PackagingParameters.VentSlotCount == 3 &&
                capNeckRoundTrip.PackagingParameters.ChamferMm == 0.3,
                ref pass, ref fail, "Параметры H/D, шага, длины, направления, каналов и фаски сохраняются для последующего стандартного редактирования.");
            ThreadMetadataRecord capNeckOverflow = CloneRecord(capNeckRecord);
            capNeckOverflow.PackagingParameters.ResolvedAxialLengthMm = 70.0;
            Check("CAPNECK-METADATA-REJECT-AXIAL-OVERFLOW", !capNeckOverflow.IsValid(), ref pass, ref fail,
                "Сохранённая длина группы резьбы крышек/горловин, выходящая за исходную цилиндрическую поверхность, не допускается.");
            Check("CAPNECK-INTEGRITY-INTERVAL-USES-RESOLVED-EXTENT",
                ThreadIntegrityPolicy.TryGetInterval(capNeckRecord, new[] { 0.0, 0.0, 1.0 }, out double capNeckStart, out double capNeckEnd) &&
                Math.Abs(capNeckStart - 7.875) < 1e-9 && Math.Abs(capNeckEnd - 5.675) < 1e-9,
                ref pass, ref fail, "Проверка целостности использует фактический сохранённый осевой интервал семейства крышек/горловин.");

            // Keep the preview extent policy pure and testable without Inventor COM.
            Check("PREVIEW-LENGTH-FULL", Math.Abs(ThreadPreviewLengthPolicy.ResolveLengthCm(true, 3.0, 5.0, 20.0) - 2.5) < 1e-9,
                ref pass, ref fail, "При полном режиме предпросмотр использует длину поверхности за вычетом отступа.");
            Check("PREVIEW-LENGTH-LIMITED", Math.Abs(ThreadPreviewLengthPolicy.ResolveLengthCm(false, 3.0, 5.0, 12.0) - 1.2) < 1e-9,
                ref pass, ref fail, "При выключенном полном режиме предпросмотр использует поле длины в мм.");
            Check("PREVIEW-LENGTH-OFFSET", Math.Abs(ThreadPreviewLengthPolicy.ResolveLengthCm(false, 3.0, 10.0, 15.0) - 1.5) < 1e-9,
                ref pass, ref fail, "Ограниченная длина начинается после указанного отступа.");
            Check("PREVIEW-LENGTH-REJECT-OVERFLOW", ThrowsInvalidOperation(() => ThreadPreviewLengthPolicy.ResolveLengthCm(false, 1.0, 0.0, 20.0)),
                ref pass, ref fail, "Отклоняется ограниченная длина, выходящая за поверхность.");
            Check("PREVIEW-LENGTH-REJECT-ZERO", ThrowsInvalidOperation(() => ThreadPreviewLengthPolicy.ResolveLengthCm(false, 3.0, 0.0, 0.0)),
                ref pass, ref fail, "Отклоняется нулевая длина предпросмотра.");

            // Native Inventor cosmetic ThreadFeatures are converted by the regular Helivanta
            // physical-geometry pipeline. Pure mapping rules are kept testable without COM.
            Check("NATIVE-THREAD-MM-TO-CM", NativeThreadConversionPolicy.NearlyEqual(
                NativeThreadConversionPolicy.MillimetersToDatabaseCentimeters(25.0), 2.5),
                ref pass, ref fail, "Глубина/смещение в мм переводятся в сантиметры базы Inventor.");
            Check("NATIVE-THREAD-CM-TO-MM", NativeThreadConversionPolicy.NearlyEqual(
                NativeThreadConversionPolicy.DatabaseCentimetersToMillimeters(1.25), 12.5),
                ref pass, ref fail, "Значения Parameter из базы Inventor отображаются в миллиметрах.");
            double nativeDepthMm, nativeOffsetMm, nativeLengthMm;
            string nativeLengthError;
            Check("NATIVE-LENGTH-FULL-DEPTH-ALLOWS-MISSING-DEPTH",
                NativeThreadConversionPolicy.TryResolveNativeThreadLengths(true, false, 0.0, true, 0.0, 2.5,
                    out nativeDepthMm, out nativeOffsetMm, out nativeLengthMm, out nativeLengthError) &&
                nativeDepthMm == 0.0 && nativeOffsetMm == 0.0 && Math.Abs(nativeLengthMm - 25.0) < 1e-9,
                ref pass, ref fail, "Полная штатная резьба может использовать длину цилиндра, если ThreadDepth Parameter недоступен.");
            Check("NATIVE-LENGTH-LIMITED-REQUIRES-DEPTH",
                !NativeThreadConversionPolicy.TryResolveNativeThreadLengths(false, false, 0.0, true, 0.0, 2.5,
                    out nativeDepthMm, out nativeOffsetMm, out nativeLengthMm, out nativeLengthError),
                ref pass, ref fail, "Ограниченная штатная резьба без ThreadDepth отклоняется до транзакции.");
            Check("NATIVE-LENGTH-MISSING-OFFSET-IS-ZERO",
                NativeThreadConversionPolicy.TryResolveNativeThreadLengths(false, true, 1.25, false, 0.0, 2.5,
                    out nativeDepthMm, out nativeOffsetMm, out nativeLengthMm, out nativeLengthError) &&
                Math.Abs(nativeDepthMm - 12.5) < 1e-9 && nativeOffsetMm == 0.0 && Math.Abs(nativeLengthMm - 12.5) < 1e-9,
                ref pass, ref fail, "Отсутствующий ThreadOffset трактуется как нулевое смещение, без разыменования null.");
            Check("NATIVE-LENGTH-REJECTS-NEGATIVE-OFFSET",
                !NativeThreadConversionPolicy.TryResolveNativeThreadLengths(true, false, 0.0, true, -0.1, 2.5,
                    out nativeDepthMm, out nativeOffsetMm, out nativeLengthMm, out nativeLengthError),
                ref pass, ref fail, "Отрицательное смещение штатной резьбы отклоняется до открытия транзакции.");
            // Reuse nativeDepthMm/nativeOffsetMm/nativeLengthMm/nativeLengthError declared above.
            double inferredStartStation;
            Check("NATIVE-START-EDGE-FALLBACK-FORWARD-STATION",
                NativeThreadConversionPolicy.TryResolveStartStation(new[] { 2.5, 0.0, 1.25 }, false, out inferredStartStation) &&
                Math.Abs(inferredStartStation - 0.0) < 1e-9,
                ref pass, ref fail, "Если StartEdge пуст, для обычного направления выбирается минимальная осевая позиция ThreadedFace.");
            Check("NATIVE-START-EDGE-FALLBACK-REVERSED-STATION",
                NativeThreadConversionPolicy.TryResolveStartStation(new[] { 2.5, 0.0, 1.25 }, true, out inferredStartStation) &&
                Math.Abs(inferredStartStation - 2.5) < 1e-9,
                ref pass, ref fail, "Для реверсированного направления выбирается максимальная осевая позиция ThreadedFace.");
            Check("NATIVE-START-EDGE-FALLBACK-REJECTS-EMPTY",
                !NativeThreadConversionPolicy.TryResolveStartStation(Array.Empty<double>(), false, out inferredStartStation),
                ref pass, ref fail, "Пустой набор границ ThreadedFace не даёт произвольно выбирать старт.");
            Check("NATIVE-START-EDGE-FALLBACK-REJECTS-NONFINITE",
                !NativeThreadConversionPolicy.TryResolveStartStation(new[] { 0.0, double.NaN }, false, out inferredStartStation),
                ref pass, ref fail, "Некорректные координаты границ ThreadedFace отклоняются.");

            Check("NATIVE-START-PREFERS-FREE-END-WHEN-NATIVE-START-IS-SHOULDER",
                NativeThreadConversionPolicy.ShouldReverseStartToPreferFreeEnd(true, false),
                ref pass, ref fail, "Если штатное начало оказалось у плеча, а противоположный край свободен, физическая резьба начинается со свободного края.");
            Check("NATIVE-START-PRESERVES-FREE-START",
                !NativeThreadConversionPolicy.ShouldReverseStartToPreferFreeEnd(false, true),
                ref pass, ref fail, "Уже выбранный свободный край остаётся началом, даже если противоположный конец является плечом.");
            Check("NATIVE-START-AMBIGUOUS-BOTH-SHOULDERS-DOES-NOT-FLIP",
                !NativeThreadConversionPolicy.ShouldReverseStartToPreferFreeEnd(true, true),
                ref pass, ref fail, "Если оба конца являются плечами, эвристика не меняет направление без однозначного свободного края.");
            Check("NATIVE-START-OPPOSITE-END-REQUIRES-COMPLETE-INSPECTION",
                NativeThreadConversionPolicy.IsOppositeEndInspectionComplete(3, 3, false) &&
                !NativeThreadConversionPolicy.IsOppositeEndInspectionComplete(3, 3, true) &&
                !NativeThreadConversionPolicy.IsOppositeEndInspectionComplete(3, 2, false) &&
                !NativeThreadConversionPolicy.IsOppositeEndInspectionComplete(-1, 0, false),
                ref pass, ref fail,
                "Уникальный свободный край разрешается выбирать только после проверки каждого сохранённого противоположного ребра; ошибка чтения и неполный обход блокируют выбор.");

            string packagingSelectionRestoreSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8) : string.Empty;
            string threadDialogSelectionRestoreSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8) : string.Empty;
            bool selectionRestorationIsFailClosed =
                SelectionRestorationPolicy.IsUniqueReferenceKeyCollectionMatch(1) &&
                !SelectionRestorationPolicy.IsUniqueReferenceKeyCollectionMatch(0) &&
                !SelectionRestorationPolicy.IsUniqueReferenceKeyCollectionMatch(2) &&
                SelectionRestorationPolicy.CanFallbackToOriginalEdge(false) &&
                !SelectionRestorationPolicy.CanFallbackToOriginalEdge(true) &&
                SelectionRestorationPolicy.CanContinueAutoPitchAfterAbort(true, true) &&
                !SelectionRestorationPolicy.CanContinueAutoPitchAfterAbort(false, true) &&
                !SelectionRestorationPolicy.CanContinueAutoPitchAfterAbort(true, false) &&
                !SelectionRestorationPolicy.CanContinueAutoPitchAfterAbort(false, false) &&
                SelectionRestorationPolicy.IsSelectionSnapshotVerified(2, 2, 2, 0) &&
                !SelectionRestorationPolicy.IsSelectionSnapshotVerified(2, 1, 1, 0) &&
                !SelectionRestorationPolicy.IsSelectionSnapshotVerified(2, 2, 1, 0) &&
                !SelectionRestorationPolicy.IsSelectionSnapshotVerified(2, 2, 2, 1) &&
                packagingSelectionRestoreSource.Contains("IsUniqueReferenceKeyCollectionMatch(matchCount)") &&
                packagingSelectionRestoreSource.Contains("CanFallbackToOriginalEdge(true)") &&
                packagingSelectionRestoreSource.Contains("ComObjectIdentity.Compare(edgeToSelect, selectedEdge") &&
                packagingSelectionRestoreSource.Contains("CanContinueAutoPitchAfterAbort(selectionRestored, restoredSelectedEdge != null)") &&
                packagingSelectionRestoreSource.Contains("selectedEdge = restoredSelectedEdge;") &&
                packagingSelectionRestoreSource.Contains("PackagingThreadDialog.Error.AutoPitchSelectionRestoreFailed") &&
                threadDialogSelectionRestoreSource.Contains("TryRestoreNativeSelectionSnapshot(nativeSelection, previousSelection") &&
                threadDialogSelectionRestoreSource.Contains("IsSelectionSnapshotVerified(");
            int nativeNextMethodStart = threadDialogSelectionRestoreSource.IndexOf(
                "private void RequestNextNativeConversionSelection()", StringComparison.Ordinal);
            int nativeNextMethodEnd = nativeNextMethodStart >= 0
                ? threadDialogSelectionRestoreSource.IndexOf(
                    "internal void RejectNextNativeConversionSelection", nativeNextMethodStart, StringComparison.Ordinal)
                : -1;
            int nativeClearHelperStart = threadDialogSelectionRestoreSource.IndexOf(
                "internal static bool TryClearNativeSelectionAndVerify", StringComparison.Ordinal);
            int nativeClearHelperEnd = nativeClearHelperStart >= 0
                ? threadDialogSelectionRestoreSource.IndexOf(
                    "private static bool TryRestoreNativeSelectionSnapshot", nativeClearHelperStart, StringComparison.Ordinal)
                : -1;
            string nativeNextMethodSource = nativeNextMethodStart >= 0 && nativeNextMethodEnd > nativeNextMethodStart
                ? threadDialogSelectionRestoreSource.Substring(nativeNextMethodStart, nativeNextMethodEnd - nativeNextMethodStart)
                : string.Empty;
            string nativeClearHelperSource = nativeClearHelperStart >= 0 && nativeClearHelperEnd > nativeClearHelperStart
                ? threadDialogSelectionRestoreSource.Substring(nativeClearHelperStart, nativeClearHelperEnd - nativeClearHelperStart)
                : string.Empty;
            int clearCallPosition = nativeNextMethodSource.IndexOf("TryClearNativeSelectionAndVerify(_app", StringComparison.Ordinal);
            int nextHandlerPosition = nativeNextMethodSource.IndexOf("Action handler = EditSelectionRequested", StringComparison.Ordinal);
            bool nativeNextRequiresVerifiedEmptySelection = clearCallPosition >= 0 && nextHandlerPosition > clearCallPosition &&
                nativeNextMethodSource.Contains("_nativeConversionAwaitingNextSelection = false;") &&
                nativeNextMethodSource.Contains("bBuild.Enabled = true;") &&
                nativeNextMethodSource.Contains("ThreadDialog.Status.NativeConversion.SelectSetClearFailed") &&
                nativeNextMethodSource.Contains("return;") &&
                nativeClearHelperSource.Contains("nativeSelection.Clear();") &&
                nativeClearHelperSource.Contains("countAfter = nativeSelection.Count;") &&
                nativeClearHelperSource.Contains("if (countAfter != 0)") &&
                nativeClearHelperSource.Contains("return false;");
            Check("NATIVE-SEQUENCE-NEXT-PICK-REQUIRES-VERIFIED-EMPTY-SELECTSET",
                nativeNextRequiresVerifiedEmptySelection, ref pass, ref fail,
                "The next native Browser pick may start only after the real Inventor SelectSet is cleared and its empty count is read back; clear/readback uncertainty restores a retryable Next state and must not start the picker.");

            string runtimeAuditNativeSelectionSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8) : string.Empty;
            int nativeClearRuntimeMethodStart = runtimeAuditNativeSelectionSource.IndexOf(
                "private void RunNativeConversionNextSelectionClearCase", StringComparison.Ordinal);
            int nativeClearRuntimeMethodEnd = nativeClearRuntimeMethodStart >= 0
                ? runtimeAuditNativeSelectionSource.IndexOf("private static void RunRunoutTemporaryCleanupFailureCase",
                    nativeClearRuntimeMethodStart, StringComparison.Ordinal)
                : -1;
            string nativeClearRuntimeMethodSource = nativeClearRuntimeMethodStart >= 0 &&
                nativeClearRuntimeMethodEnd > nativeClearRuntimeMethodStart
                ? runtimeAuditNativeSelectionSource.Substring(nativeClearRuntimeMethodStart,
                    nativeClearRuntimeMethodEnd - nativeClearRuntimeMethodStart)
                : string.Empty;
            bool nativeSelectionClearRuntimeScenario = runtimeAuditNativeSelectionSource.Contains("NATIVE-CONVERSION-NEXT-SELECTION-CLEAR-READBACK") &&
                runtimeAuditNativeSelectionSource.Contains("RunNativeConversionNextSelectionClearCase(result)") &&
                nativeClearRuntimeMethodSource.Contains("selectSet.Select(selectedEdge)") &&
                nativeClearRuntimeMethodSource.Contains("TryClearNativeSelectionAndVerify(_app") &&
                nativeClearRuntimeMethodSource.Contains("SelectSetCountBeforeClear") &&
                nativeClearRuntimeMethodSource.Contains("SelectSetCountAfterClear");
            Check("RUNTIME-AUDIT-NATIVE-CONVERSION-NEXT-SELECTION-CLEAR-READBACK",
                nativeSelectionClearRuntimeScenario, ref pass, ref fail,
                "Inventor Runtime Audit must select one actual Edge in a temporary IPT, invoke the production SelectSet clear helper, and verify the actual COM collection count transitions from one to zero before a next Browser pick can proceed.");
            Check("DIALOG-SELECTION-RESTORATION-REQUIRES-UNIQUE-AND-VERIFIED-COM-IDENTITY",
                selectionRestorationIsFailClosed, ref pass, ref fail,
                "A captured reference key cannot fall back to a stale Edge RCW; ObjectCollection results require exactly one match; automatic pitch retries require the verified restored Edge returned by SelectSet readback; mode-switch rollback verifies the restored SelectSet count and each COM identity.");

            string runtimeAuditAutoPitchSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8) : string.Empty;
            string packagingBuilderAutoPitchSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadGeometryBuilder.cs"), Encoding.UTF8) : string.Empty;
            string autoPitchRuntimeProbeSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingAutoPitchRuntimeAuditProbe.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingAutoPitchRuntimeAuditProbe.cs"), Encoding.UTF8) : string.Empty;
            int autoPitchTransactionStart = packagingBuilderAutoPitchSource.IndexOf("transaction = app.TransactionManager.StartTransaction", StringComparison.Ordinal);
            int autoPitchFailureInjection = packagingBuilderAutoPitchSource.IndexOf("ShouldInjectFailureAfterTransactionStarted(doc, selectedEdge, p.PitchMm)", StringComparison.Ordinal);
            int autoPitchAbort = packagingBuilderAutoPitchSource.IndexOf("transaction.Abort();", StringComparison.Ordinal);
            bool liveAutoPitchRestoreRuntimeScenario = runtimeAuditAutoPitchSource.Contains("CAPNECK-AUTOPITCH-FAILED-FIRST-CAD-ATTEMPT-RESTORED-EDGE-RETRY") &&
                runtimeAuditAutoPitchSource.Contains("RunCapNeckAutoPitchSelectionRestoreRetryCase(root, runId, result)") &&
                runtimeAuditAutoPitchSource.Contains("RunRuntimeAuditAutoPitchBuild(4.0, 24.0") &&
                runtimeAuditAutoPitchSource.Contains("probe.FirstAttemptAbortSucceeded") &&
                runtimeAuditAutoPitchSource.Contains("probe.RestoredSelectionVerified") &&
                runtimeAuditAutoPitchSource.Contains("probe.RetryUsedRestoredEdge") &&
                runtimeAuditAutoPitchSource.Contains("Math.Abs(group.Record.PackagingParameters.PitchMm - probe.RetryPitchMm)") &&
                runtimeAuditAutoPitchSource.Contains("PackagingThreadGeometryPolicy.AutoPitchStepMm") &&
                autoPitchTransactionStart >= 0 && autoPitchFailureInjection > autoPitchTransactionStart &&
                autoPitchAbort > autoPitchFailureInjection &&
                autoPitchRuntimeProbeSource.Contains("RuntimeAuditAccessGate.IsEnabled()") &&
                autoPitchRuntimeProbeSource.Contains("ComObjectIdentity.Compare(selectedEdge, restoredEdge") &&
                autoPitchRuntimeProbeSource.Contains("_firstAttemptAbortSucceeded = abortSucceeded") &&
                packagingSelectionRestoreSource.Contains("PackagingAutoPitchRuntimeAuditProbe.RecordRestoredSelection(doc, selectedEdge)");
            Check("RUNTIME-AUDIT-CAPNECK-AUTOPITCH-FAILED-FIRST-ATTEMPT-RETRIES-WITH-RESTORED-EDGE",
                liveAutoPitchRestoreRuntimeScenario, ref pass, ref fail,
                "Inventor Runtime Audit must run the real Cap/Neck AutoPitch dialog, inject one failure after a real transaction starts, verify Abort and SelectSet restoration, prove the next BuildCore receives the same restored COM Edge, and validate the retry increment against the shared AutoPitch policy step.");

            Check("NATIVE-SEQUENCE-NOT-IN-CONVERSION-MODE",
                NativeThreadConversionSequencePolicy.Resolve(false, false, false) == NativeThreadConversionSequenceState.NotInConversionMode,
                ref pass, ref fail, "Обычный диалог Helivanta не переходит в последовательный выбор штатных резьб.");
            Check("NATIVE-SEQUENCE-FIRST-THREAD-READY-TO-CONVERT",
                NativeThreadConversionSequencePolicy.Resolve(true, false, false) == NativeThreadConversionSequenceState.ReadyToConvertCurrent,
                ref pass, ref fail, "Перед первым успешным преобразованием кнопка запускает конвертацию текущей ThreadFeature.");
            Check("NATIVE-SEQUENCE-UNDO-RETURNS-TO-CURRENT-THREAD",
                NativeThreadConversionSequencePolicy.Resolve(true, false, false) == NativeThreadConversionSequenceState.ReadyToConvertCurrent,
                ref pass, ref fail, "После отмены успешной конвертации текущая ThreadFeature снова доступна для изменения без повторного выбора.");
            Check("NATIVE-SEQUENCE-COMPLETED-REQUESTS-NEXT",
                NativeThreadConversionSequencePolicy.Resolve(true, true, false) == NativeThreadConversionSequenceState.RequestNextSelection,
                ref pass, ref fail, "После успешного преобразования действие кнопки запрашивает следующую штатную резьбу.");
            Check("NATIVE-SEQUENCE-WAITING-FOR-NEXT-FEATURE",
                NativeThreadConversionSequencePolicy.Resolve(true, true, true) == NativeThreadConversionSequenceState.AwaitingNextSelection,
                ref pass, ref fail, "После команды «Следующая» ожидается выбор другой ThreadFeature до загрузки нового плана.");
            Check("NATIVE-SEQUENCE-REJECTS-WAITING-WITHOUT-COMPLETION",
                NativeThreadConversionSequencePolicy.Resolve(true, false, true) == NativeThreadConversionSequenceState.InvalidState,
                ref pass, ref fail, "Нельзя запрашивать следующую резьбу, пока текущее преобразование не завершено.");
            Check("NATIVE-SEQUENCE-ACCEPTS-NEW-PLAN-ONLY-AFTER-NEXT",
                NativeThreadConversionSequencePolicy.CanAcceptNextFeature(true, true, true) &&
                !NativeThreadConversionSequencePolicy.CanAcceptNextFeature(true, true, false) &&
                !NativeThreadConversionSequencePolicy.CanAcceptNextFeature(true, false, true),
                ref pass, ref fail, "Новый план принимается только после успеха предыдущей конвертации и явного запроса следующего выбора.");

            Check("NATIVE-HISTORY-LAST-FEATURE-NO-DOWNSTREAM",
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(11, 10) == 0,
                ref pass, ref fail, "Последняя штатная резьба в истории не имеет последующих операций.");
            Check("NATIVE-HISTORY-FIRST-FEATURE-COUNT",
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(11, 0) == 10,
                ref pass, ref fail, "Для первой операции число последующих элементов равно общему числу минус один.");
            Check("NATIVE-HISTORY-MIDDLE-FEATURE-COUNT",
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(11, 5) == 5,
                ref pass, ref fail, "Для операции в середине истории считаются только элементы после неё.");
            Check("NATIVE-HISTORY-REJECTS-NEGATIVE-INDEX",
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(11, -1) == -1,
                ref pass, ref fail, "Отрицательный индекс истории не даёт ложного количества последующих операций.");
            Check("NATIVE-HISTORY-REJECTS-OUT-OF-RANGE-INDEX",
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(11, 11) == -1 &&
                NativeThreadConversionPolicy.ResolveDownstreamOperationCount(0, 0) == -1,
                ref pass, ref fail, "Пустая история и индекс за границей отклоняются.");

            NativeThreadConversionPolicy.ProfileDefinition nativeProfile;
            string nativeConversionError;
            Check("GOST-METRIC-THREAD-STANDARD-ID-APPENDED", (int)ThreadStandard.GostMetric == 6 &&
                (int)ThreadStandard.ISO == 0 && (int)ThreadStandard.Rd == 5,
                ref pass, ref fail, "ГОСТ добавлен новым ID 6; все существующие постоянные ID 0–5 сохранены.");
            Check("GOST-METRIC-CATALOGUE-IS-SEPARATE", ThreadStandards.Sizes(ThreadStandard.GostMetric).Count > 0 &&
                ThreadStandards.Sizes(ThreadStandard.GostMetric).Count != ThreadStandards.Sizes(ThreadStandard.ISO).Count,
                ref pass, ref fail, "ГОСТ использует отдельную нормативную таблицу, не каталог Inventor ISO.");
            Check("GOST-METRIC-FOLDER-LABEL", ThreadFolderNamePolicy.StandardLabel(ThreadStandard.GostMetric) == "ГОСТ 9150/8724",
                ref pass, ref fail, "В дереве модели ГОСТ обозначается собственной нормативной меткой.");
            Check("GOST-METRIC-FOLDER-LABEL-EN", ThreadFolderNamePolicy.StandardLabel(ThreadStandard.GostMetric, "en") == "GOST 9150/8724",
                ref pass, ref fail, "Английская метка папки ГОСТ локализована.");
            Check("GOST-METRIC-FOLDER-NAME", ThreadFolderNamePolicy.BuildThreadFolderName("M10 x 1.5", 1, true, ThreadStandard.GostMetric, false) ==
                "Резьба — ГОСТ 9150/8724 · M10 x 1.5 · 1 заход · Правая (R)",
                ref pass, ref fail, "Папка модели сохраняет семейство ГОСТ и выбранное нормативное обозначение.");
            Check("GOST-METRIC-SIZE-NAME", ThreadSizeNamePolicy.Build(ThreadStandard.GostMetric,
                ThreadStandards.Sizes(ThreadStandard.GostMetric), 10.0, 1.5) == "M10 x 1.5",
                ref pass, ref fail, "Обозначение типоразмера ГОСТ строится из собственного нормативного каталога.");
            Check("GOST-METRIC-NORMATIVE-CHAIN-IS-EXPLICIT",
                ThreadStandards.GostMetricProfileStandard == "ГОСТ 9150-2002" &&
                ThreadStandards.GostMetricDiameterPitchStandard == "ГОСТ 8724-2002" &&
                ThreadStandards.GostMetricBasicDimensionsStandard == "ГОСТ 24705-2004",
                ref pass, ref fail, "ГОСТ 9150 задаёт профиль, ГОСТ 8724 — допустимые диаметры/шаги, ГОСТ 24705 — основные размеры.");
            GostMetricBasicDimensions gostM10x15 = ThreadStandards.CalculateGostMetricBasicDimensions(10.0, 1.5);
            Check("GOST-24705-M10X1.5-TABLE-1-CONTROL-VALUES",
                Math.Abs(Math.Round(gostM10x15.PitchDiameterMm, 3, MidpointRounding.AwayFromZero) - 9.026) < 1e-9 &&
                Math.Abs(Math.Round(gostM10x15.InternalMinorDiameterMm, 3, MidpointRounding.AwayFromZero) - 8.376) < 1e-9 &&
                Math.Abs(Math.Round(gostM10x15.ExternalMinorDiameterMm, 3, MidpointRounding.AwayFromZero) - 8.376) < 1e-9 &&
                Math.Abs(Math.Round(gostM10x15.ExternalStressRootDiameterMm, 3, MidpointRounding.AwayFromZero) - 8.160) < 1e-9,
                ref pass, ref fail, "M10×1,5: D2/d2=9,026; D1=8,376; d1=8,376; d3=8,160 мм по таблице 1 ГОСТ 24705-2004.");
            GostMetricBasicDimensions gostM1x02 = ThreadStandards.CalculateGostMetricBasicDimensions(1.0, 0.2);
            GostMetricBasicDimensions gostM600x8 = ThreadStandards.CalculateGostMetricBasicDimensions(600.0, 8.0);
            Check("GOST-24705-TABLE-1-CONTROL-VALUES-COVER-SMALL-AND-LARGE-RANGE",
                Math.Abs(Math.Round(gostM1x02.PitchDiameterMm, 3, MidpointRounding.AwayFromZero) - 0.870) < 1e-9 &&
                Math.Abs(Math.Round(gostM1x02.InternalMinorDiameterMm, 3, MidpointRounding.AwayFromZero) - 0.783) < 1e-9 &&
                Math.Abs(Math.Round(gostM1x02.ExternalStressRootDiameterMm, 3, MidpointRounding.AwayFromZero) - 0.755) < 1e-9 &&
                Math.Abs(Math.Round(gostM600x8.PitchDiameterMm, 3, MidpointRounding.AwayFromZero) - 594.804) < 1e-9 &&
                Math.Abs(Math.Round(gostM600x8.InternalMinorDiameterMm, 3, MidpointRounding.AwayFromZero) - 591.340) < 1e-9 &&
                Math.Abs(Math.Round(gostM600x8.ExternalStressRootDiameterMm, 3, MidpointRounding.AwayFromZero) - 590.185) < 1e-9,
                ref pass, ref fail, "Контрольные строки ГОСТ 24705 проверяются на малом M1×0,2 и верхнем M600×8 диапазона.");
            ThreadProfileSpec gostMetricProfile = ThreadStandards.Spec(ThreadStandard.GostMetric);
            Groove gostM10x15ExternalGroove = ThreadStandards.ComputeGroove(gostMetricProfile, true, 1.5, 0.0, 1.5);
            Check("GOST-24705-DIMENSIONS-REMAIN-CONSISTENT-WITH-BREP-BASIC-PROFILE",
                Math.Abs(10.0 - 2.0 * gostM10x15ExternalGroove.Depth - gostM10x15.ExternalStressRootDiameterMm) < 1e-9 &&
                Math.Abs(10.0 - 2.0 * ThreadStandards.MinorOffset(gostMetricProfile, 1.5) - gostM10x15.InternalMinorDiameterMm) < 1e-9 &&
                Math.Abs(gostMetricProfile.AngleDeg - 60.0) < 1e-12 &&
                Math.Abs(gostMetricProfile.DepthExt - ThreadStandards.Spec(ThreadStandard.ISO).DepthExt) < 1e-12 &&
                Math.Abs(gostMetricProfile.DepthInt - ThreadStandards.Spec(ThreadStandard.ISO).DepthInt) < 1e-12,
                ref pass, ref fail, "Существующая геометрия остаётся неизменной: наружная глубина соответствует d3, внутренняя — D1; профиль ГОСТ 9150 сохраняет гармонизированные параметры ISO 68-1.");
            bool gostUnlistedPairRejected = false;
            try { ThreadStandards.CalculateGostMetricBasicDimensions(10.0, 1.12); }
            catch (ArgumentException) { gostUnlistedPairRejected = true; }
            Check("GOST-24705-CALCULATOR-REJECTS-PAIR-OUTSIDE-GOST-8724-CATALOG",
                gostUnlistedPairRejected,
                ref pass, ref fail, "Расчёт по цепочке ГОСТ не маркирует произвольную пару диаметр/шаг как нормативную, если её нет в каталоге ГОСТ 8724.");
            Check("GOST-METRIC-DOES-NOT-ACCEPT-ISO-TOLERANCE",
                !ThreadTolerancePolicy.TryNormalize(ThreadStandard.GostMetric, false, "6H", out _),
                ref pass, ref fail, "Классы ISO 965 не выдаются за допуски ГОСТ 9000/16093.");
            Check("NATIVE-CONVERT-MAPS-ISO", NativeThreadConversionPolicy.TryResolveProfile(
                "ISO Metric profile", "M10x1.5", "10", true, 1.5,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.ISO &&
                Math.Abs(nativeProfile.DiameterMm - 10.0) < 1e-9 && Math.Abs(nativeProfile.PitchMm - 1.5) < 1e-9,
                ref pass, ref fail, "Штатная ISO M10×1,5 сопоставляется с физическим профилем Helivanta ISO.");
            Check("NATIVE-CONVERT-MAPS-TR-MULTISTART", NativeThreadConversionPolicy.TryResolveProfile(
                "ISO Metric Trapezoidal", "Tr30x6(P3)", "30", true, 3.0,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.Tr &&
                Math.Abs(nativeProfile.DiameterMm - 30.0) < 1e-9 && Math.Abs(nativeProfile.PitchMm - 3.0) < 1e-9 && nativeProfile.Starts == 2,
                ref pass, ref fail, "Tr30×6(P3) преобразуется с ходом 6 мм, шагом 3 мм и двумя заходами.");
            Check("NATIVE-CONVERT-MAPS-RD", NativeThreadConversionPolicy.TryResolveProfile(
                "DIN 405 Round Thread", "Rd45x4", "45", true, 4.0,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.Rd &&
                Math.Abs(nativeProfile.DiameterMm - 45.0) < 1e-9 && Math.Abs(nativeProfile.PitchMm - 4.0) < 1e-9,
                ref pass, ref fail, "Штатная круглая резьба DIN 405 сопоставляется профилю Rd.");
            Check("NATIVE-CONVERT-MAPS-BUTTRESS-S", NativeThreadConversionPolicy.TryResolveProfile(
                "DIN 513 S Buttress", "S20x4", "20", true, 4.0,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.S,
                ref pass, ref fail, "Штатная упорная резьба DIN 513 сопоставляется профилю S.");
            Check("NATIVE-CONVERT-MAPS-BSW-INCH", NativeThreadConversionPolicy.TryResolveProfile(
                "British Standard Whitworth", "1/2-12 BSW", "1/2", false, 1.0 / 12.0,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.BSW &&
                Math.Abs(nativeProfile.DiameterMm - 12.7) < 1e-8 && Math.Abs(nativeProfile.PitchMm - 25.4 / 12.0) < 1e-8,
                ref pass, ref fail, "Дюймовая резьба BSW 1/2-12 переводится в миллиметры и профиль BSW.");
            Check("NATIVE-CONVERT-MAPS-ANSI-UNC", NativeThreadConversionPolicy.TryResolveProfile(
                "ANSI Unified Screw Threads", "1/2-13 UNC", "1/2", false, 1.0 / 13.0,
                out nativeProfile, out nativeConversionError) && nativeProfile.Standard == ThreadStandard.ANSI &&
                Math.Abs(nativeProfile.DiameterMm - 12.7) < 1e-8 && Math.Abs(nativeProfile.PitchMm - 25.4 / 13.0) < 1e-8,
                ref pass, ref fail, "Дюймовая резьба UNC переводится в миллиметры и профиль ANSI/UN.");
            Check("NATIVE-CONVERT-REJECTS-UNKNOWN-PROFILE", !NativeThreadConversionPolicy.TryResolveProfile(
                "National Pipe Taper", "1/2-14 NPT", "1/2", false, 1.0 / 14.0,
                out nativeProfile, out nativeConversionError) && nativeProfile == null,
                ref pass, ref fail, "Неизвестный или конический профиль не угадывается и не изменяется автоматически.");
            Check("NATIVE-CONVERT-REJECTS-UNIT-MISMATCH", !NativeThreadConversionPolicy.TryResolveProfile(
                "ISO Metric profile", "M10x1.5", "M10", false, 1.5,
                out nativeProfile, out nativeConversionError) && nativeProfile == null,
                ref pass, ref fail, "Несовпадение metric-флага и распознанного стандарта отклоняется без конвертации.");
            Check("NATIVE-CONVERT-REJECTS-ZERO-PITCH", !NativeThreadConversionPolicy.TryResolveProfile(
                "ISO Metric profile", "M10x1.5", "10", true, 0.0,
                out nativeProfile, out nativeConversionError) && nativeProfile == null,
                ref pass, ref fail, "Нулевой шаг отклоняется до начала транзакции.");
            double nativeDiameter;
            Check("NATIVE-CONVERT-PARSES-INCH-FRACTION", NativeThreadConversionPolicy.TryParseNominalDiameter(
                "1/2", "1/2-13 UNC", false, out nativeDiameter) && Math.Abs(nativeDiameter - 12.7) < 1e-8,
                ref pass, ref fail, "Номинальный диаметр 1/2 дюйма переводится в 12,7 мм.");
            Check("NATIVE-CONVERT-PARSES-MIXED-FRACTION", NativeThreadConversionPolicy.TryParseNominalDiameter(
                "1 1/8", "1 1/8-7 BSW", false, out nativeDiameter) && Math.Abs(nativeDiameter - 28.575) < 1e-8,
                ref pass, ref fail, "Смешанная дробь 1 1/8 дюйма разбирается без потери дробной части.");
            Check("NATIVE-CONVERT-RESOLVES-START-COUNT", NativeThreadConversionPolicy.ResolveStartsFromDesignation("Tr30x6(P3)", true, 3.0) == 2 &&
                NativeThreadConversionPolicy.ResolveStartsFromDesignation("Tr30x6", true, 3.0) == 2 &&
                NativeThreadConversionPolicy.ResolveStartsFromDesignation("M10x1.5", true, 1.5) == 1 &&
                NativeThreadConversionPolicy.ResolveStartsFromDesignation("1/2-13 UNC", false, 25.4 / 13.0) == 1,
                ref pass, ref fail, "Количество заходов определяется по lead/pitch и фактическому шагу ThreadInfo, а не по дюймовому TPI.");

            // Regression: selected edge diameter is the nominal diameter regardless of
            // internal/external type. Never match the internal minor/tap-drill diameter.
            // A non-exact nearest size is offered for explicit user confirmation; declining
            // preserves the selected edge diameter as a custom nominal size.
            Check("NOMINAL-DIAMETER-EXACT-MATCH", ThreadNominalDiameterPolicy.MatchesStandardDiameter(45.0, 45.0),
                ref pass, ref fail, "Табличный типоразмер совпадает только при совпадении номинального диаметра.");
            Check("NOMINAL-DIAMETER-NO-MINOR-MATCH", !ThreadNominalDiameterPolicy.MatchesStandardDiameter(45.0, 49.05),
                ref pass, ref fail, "Диаметр отверстия Ø45 не сопоставляется с номинальным Ø49,05 по внутреннему малому диаметру.");
            Check("NOMINAL-DIAMETER-NO-NEAREST-SNAP", !ThreadNominalDiameterPolicy.MatchesStandardDiameter(45.0, 46.0),
                ref pass, ref fail, "Ближайший табличный диаметр не заменяет выбранный Ø45 мм.");
            Check("NOMINAL-DIAMETER-PRESERVE-CUSTOM", Math.Abs(ThreadNominalDiameterPolicy.PreserveSelectedDiameter(45.0) - 45.0) < 1e-12,
                ref pass, ref fail, "Для пользовательского размера сохраняется номинальный диаметр выбранного ребра.");
            Check("NOMINAL-DIAMETER-NEAREST-IN-RANGE-45MM", ThreadNominalDiameterPolicy.TryFindClosestStandardDiameter(
                45.0, new[] { 42.7, 48.6, 60.5 }, out double nearestStandard45) && Math.Abs(nearestStandard45 - 42.7) < 1e-9,
                ref pass, ref fail, "Для Ø45 мм подбирается ближайший табличный диаметр, но UI запрашивает согласие до подстановки.");
            Check("NOMINAL-DIAMETER-OUTSIDE-CATALOG-RANGE-REMAINS-CUSTOM", !ThreadNominalDiameterPolicy.TryFindClosestStandardDiameter(
                350.0, new[] { 7.0, 42.7, 165.2 }, out double noRemoteStandard350) && noRemoteStandard350 == 0.0,
                ref pass, ref fail, "Диаметр вне диапазона таблицы не подменяется ближайшим крайним типоразмером.");

            List<ThreadSize> expandedIsoCatalogue = ThreadStandards.Sizes(ThreadStandard.ISO);
            List<ThreadSize> expandedTrCatalogue = ThreadStandards.Sizes(ThreadStandard.Tr);
            Check("THREAD-CATALOGUE-ISO-IMPORTS-ALL-499-XLS-DIAMETER-PITCH-PAIRS",
                expandedIsoCatalogue.Count == 499 &&
                expandedIsoCatalogue.Select(x => x.DiameterMm.ToString("0.#####", CultureInfo.InvariantCulture) + "|" + x.PitchMm.ToString("0.#####", CultureInfo.InvariantCulture)).Distinct().Count() == 499 &&
                expandedIsoCatalogue.Any(x => Math.Abs(x.DiameterMm - 0.25) < 1e-9 && Math.Abs(x.PitchMm - 0.075) < 1e-9) &&
                expandedIsoCatalogue.Any(x => Math.Abs(x.DiameterMm - 1060.0) < 1e-9 && Math.Abs(x.PitchMm - 8.0) < 1e-9),
                ref pass, ref fail, "Каталог ISO импортирует все 499 уникальных пар диаметра/шага из листов ISO Metric и ANSI Metric: от M0.25x0.075 до M1060x8.");
            Check("THREAD-CATALOGUE-TR-IMPORTS-ALL-378-XLS-DIAMETER-PITCH-PAIRS",
                expandedTrCatalogue.Count == 378 &&
                expandedTrCatalogue.Select(x => x.DiameterMm.ToString("0.#####", CultureInfo.InvariantCulture) + "|" + x.PitchMm.ToString("0.#####", CultureInfo.InvariantCulture)).Distinct().Count() == 378 &&
                expandedTrCatalogue.Any(x => Math.Abs(x.DiameterMm - 16.0) < 1e-9 && Math.Abs(x.PitchMm - 3.0) < 1e-9) &&
                expandedTrCatalogue.Any(x => Math.Abs(x.DiameterMm - 1120.0) < 1e-9 && Math.Abs(x.PitchMm - 8.0) < 1e-9) &&
                expandedTrCatalogue.Any(x => Math.Abs(x.DiameterMm - 1120.0) < 1e-9 && Math.Abs(x.PitchMm - 44.0) < 1e-9),
                ref pass, ref fail, "Каталог Tr импортирует все 378 уникальных пар из предоставленного листа ISO Metric Trapezoidal: от TR8 и дополнительных шагов до TR1120.");

            // A user-defined diameter has no exact table row, but its pitch dropdown
            // should still offer standard-family values plus common metric choices.
            var customRdPitches = ThreadPitchSuggestionPolicy.ForCustomDiameter(new[] { 25.4 / 6.0, 25.4 / 4.0 });
            Check("CUSTOM-PITCH-INCLUDES-2.5", customRdPitches.Any(p => Math.Abs(p - 2.5) < 1e-9),
                ref pass, ref fail, "Варианты шага для пользовательского размера включают ходовой шаг 2,5 мм.");
            Check("CUSTOM-PITCH-INCLUDES-CATALOGUE", customRdPitches.Any(p => Math.Abs(p - (25.4 / 6.0)) < 0.0001) &&
                customRdPitches.Any(p => Math.Abs(p - (25.4 / 4.0)) < 0.0001),
                ref pass, ref fail, "Варианты шага сохраняют шаги из таблицы активного стандарта.");
            Check("CUSTOM-PITCH-DESCENDING-UNIQUE", customRdPitches.SequenceEqual(customRdPitches.Distinct().OrderByDescending(p => p)),
                ref pass, ref fail, "Список пользовательских шагов отсортирован по убыванию и не содержит дубликатов.");

            // Editing must fail closed on incomplete membership, while downstream operations
            // require an explicit risk warning rather than an unconditional prohibition.
            Check("EDIT-GROUP-COMPLETE-INDICES", ThreadEditSafetyPolicy.HasCompleteIndices(4, new[] { 0, 1, 2, 3 }),
                ref pass, ref fail, "Полная группа с индексами 0..N-1 пригодна для дальнейшей проверки редактирования.");
            Check("EDIT-GROUP-REJECT-MISSING-INDEX", !ThreadEditSafetyPolicy.HasCompleteIndices(4, new[] { 0, 1, 3 }),
                ref pass, ref fail, "Группа с отсутствующим объектом отклоняется.");
            Check("EDIT-GROUP-REJECT-DUPLICATE-INDEX", !ThreadEditSafetyPolicy.HasCompleteIndices(3, new[] { 0, 1, 1 }),
                ref pass, ref fail, "Повторяющийся индекс не может маскировать неполную группу.");
            Check("EDIT-HISTORY-TAIL-ALLOWED", ThreadEditSafetyPolicy.IsAtHistoryTail(9, 10),
                ref pass, ref fail, "Группа у последней операции истории проходит консервативную проверку.");
            Check("EDIT-HISTORY-DOWNSTREAM-REQUIRES-WARNING", ThreadEditSafetyPolicy.RequiresDownstreamWarning(7, 10),
                ref pass, ref fail, "При наличии последующих операций разрешение на замену запрашивается у пользователя.");
            Check("EDIT-HISTORY-TAIL-NEEDS-NO-WARNING", !ThreadEditSafetyPolicy.RequiresDownstreamWarning(9, 10) &&
                !ThreadEditSafetyPolicy.RequiresDownstreamWarning(-1, 10),
                ref pass, ref fail, "Операция в конце истории не требует предупреждения, а неизвестный индекс не классифицируется как зависимость.");
            Check("EDIT-FOLDER-SELECTS-SINGLE-THREAD", ThreadEditSafetyPolicy.TryChooseSingleThreadId(
                new[] { "thread-A", "thread-A", "thread-A" }, out string folderThreadA) && folderThreadA == "thread-A",
                ref pass, ref fail, "Папка с несколькими объектами одной резьбы разрешается в единый ThreadId.");
            Check("EDIT-FOLDER-REJECTS-MIXED-THREADS", !ThreadEditSafetyPolicy.TryChooseSingleThreadId(
                new[] { "thread-A", "thread-B" }, out _),
                ref pass, ref fail, "Папка с объектами разных резьб блокируется как неоднозначная.");
            Check("EDIT-FOLDER-REJECTS-UNTAGGED", !ThreadEditSafetyPolicy.TryChooseSingleThreadId(
                new[] { null, "", "  " }, out _),
                ref pass, ref fail, "Пустая папка или папка без метаданных не запускает редактирование.");

            Check("EDIT-UNIQUE-TARGET-REQUIRED", ThreadEditSafetyPolicy.IsUniqueCandidateCount(1) &&
                !ThreadEditSafetyPolicy.IsUniqueCandidateCount(0) && !ThreadEditSafetyPolicy.IsUniqueCandidateCount(2),
                ref pass, ref fail, "Исходный цилиндр принимается только при одном однозначном совпадении.");
            Check("EDIT-OVERLAP-DETECTED", ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 2.0, 1.9, 3.0),
                ref pass, ref fail, "Пересекающиеся осевые интервалы распознаются как конфликт.");
            Check("EDIT-OVERLAP-REVERSED-INTERVALS", ThreadEditSafetyPolicy.IntervalsOverlap(2.0, 0.0, 1.0, 3.0),
                ref pass, ref fail, "Пересечение обнаруживается даже если один интервал передан в обратном направлении.");
            Check("EDIT-OVERLAP-IGNORES-SUBTOLERANCE-TOUCH", !ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 1.0, 0.99995, 2.0),
                ref pass, ref fail, "Микроперекрытие меньше установленного допуска не создаёт ложный конфликт.");
            Check("EDIT-OVERLAP-DETECTS-ABOVE-TOLERANCE", ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 1.0, 0.9998, 2.0),
                ref pass, ref fail, "Фактическое перекрытие больше допуска блокируется.");
            Check("EDIT-OVERLAP-NONFINITE-FAILS-CLOSED", ThreadEditSafetyPolicy.IntervalsOverlap(double.NaN, 1.0, 0.5, 1.5),
                ref pass, ref fail, "Некорректные координаты интервала считаются конфликтом, а не разрешением на редактирование.");
            Check("EDIT-SEPARATED-ZONES-ALLOWED", !ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 1.0, 1.01, 2.0),
                ref pass, ref fail, "Непересекающиеся осевые интервалы разрешены.");
            double integrityStart, integrityEnd;
            Check("INTEGRITY-INTERVAL-USES-REFERENCE-AXIS", ThreadIntegrityPolicy.TryGetInterval(original, new[] { 0.0, 0.0, 1.0 }, out integrityStart, out integrityEnd) &&
                Math.Abs(integrityStart - 7.875) < 1e-9 && Math.Abs(integrityEnd - 4.875) < 1e-9,
                ref pass, ref fail, "Интервал резьбы переводится в общую ось сравнения даже при обратном направлении исходной оси.");
            Check("INTEGRITY-INTERVAL-REJECTS-NONUNIT-REFERENCE", !ThreadIntegrityPolicy.TryGetInterval(original, new[] { 0.0, 0.0, 0.0 }, out integrityStart, out integrityEnd),
                ref pass, ref fail, "Невалидная ось сравнения не разрешает вычисление интервала.");
            Check("INTEGRITY-INTERVAL-REJECTS-OUT-OF-BOUNDS", !ThreadIntegrityPolicy.TryGetInterval(new ThreadMetadataRecord {
                SchemaVersion = 1, ThreadId = "bad-range", ObjectCount = 1,
                Parameters = new ThreadParametersSnapshot { SizeName = "M10", PitchMm = 1.0, NominalDiaMm = 10.0, Starts = 1, LengthMm = 80.0, OffsetMm = 0.0 },
                Target = new ThreadTargetSnapshot { Center = new[] { 0.0, 0.0, 0.0 }, Axis = new[] { 0.0, 0.0, 1.0 }, RadiusCm = 0.5, LengthCm = 2.0, BodyName = "Solid1" }
            }, new[] { 0.0, 0.0, 1.0 }, out integrityStart, out integrityEnd),
                ref pass, ref fail, "Интервал за пределами исходного цилиндра не принимается.");
            ThreadMetadataRecord reverseGroup = new ThreadMetadataRecord {
                SchemaVersion = 1, ThreadId = "reverse-group", ObjectCount = 1,
                Parameters = new ThreadParametersSnapshot { SizeName = "M10", PitchMm = 1.0, NominalDiaMm = 10.0, Starts = 1, LengthMm = 20.0, OffsetMm = 0.0 },
                Target = new ThreadTargetSnapshot { Center = new[] { 10.25, -4.5, 5.0 }, Axis = new[] { 0.0, 0.0, 1.0 }, RadiusCm = 1.0, LengthCm = 6.2, BodyName = "Solid1" }
            };
            double reverseStart, reverseEnd;
            Check("INTEGRITY-OVERLAP-COMMON-AXIS-REVERSED", ThreadIntegrityPolicy.TryGetInterval(original, new[] { 0.0, 0.0, 1.0 }, out integrityStart, out integrityEnd) &&
                ThreadIntegrityPolicy.TryGetInterval(reverseGroup, new[] { 0.0, 0.0, 1.0 }, out reverseStart, out reverseEnd) &&
                ThreadIntegrityPolicy.IntervalsOverlap(integrityStart, integrityEnd, reverseStart, reverseEnd),
                ref pass, ref fail, "Пересечение определяется корректно для групп с противоположным направлением оси.");
            Check("INTEGRITY-OVERLAP-NONFINITE-FAILS-CLOSED", ThreadIntegrityPolicy.IntervalsOverlap(double.NaN, 1.0, 0.5, 1.5),
                ref pass, ref fail, "Некорректные координаты при проверке целостности трактуются как конфликт.");
            ThreadTargetSnapshot overlapCandidate = new ThreadTargetSnapshot {
                Center = new[] { 0.0, 0.0, 0.0 }, Axis = new[] { 0.0, 0.0, 1.0 },
                RadiusCm = 0.5, LengthCm = 1.0, IsExternal = true, BodyName = "Solid1"
            };
            ThreadMetadataRecord persistedOverlap = CreateOverlapTestRecord("persisted-overlap", 0.0, 2.0, "Solid1");
            string persistedConflict, persistedPolicyError;
            Check("PERSISTED-OVERLAP-POLICY-REJECTS-CREATE-CONFLICT",
                !ThreadMetadataOverlapPolicy.TryValidateNoOverlap(overlapCandidate, 0.0, 1.0,
                    new[] { persistedOverlap }, null, out persistedConflict, out persistedPolicyError) &&
                persistedConflict == "persisted-overlap",
                ref pass, ref fail, "Новая операция блокируется при перекрытии с ранее сохранённой группой, даже если она создана в другом сеансе диалога.");
            ThreadTargetSnapshot separatedCandidate = new ThreadTargetSnapshot {
                Center = new[] { 0.0, 0.0, 2.1 }, Axis = new[] { 0.0, 0.0, 1.0 },
                RadiusCm = 0.5, LengthCm = 1.0, IsExternal = true, BodyName = "Solid1"
            };
            Check("PERSISTED-OVERLAP-POLICY-ALLOWS-SEPARATED-AXIAL-ZONES",
                ThreadMetadataOverlapPolicy.TryValidateNoOverlap(separatedCandidate, 2.1, 3.1,
                    new[] { persistedOverlap }, null, out persistedConflict, out persistedPolicyError),
                ref pass, ref fail, "Разнесённые по оси участки одной цилиндрической поверхности остаются допустимыми.");
            Check("PERSISTED-OVERLAP-POLICY-EDIT-EXCLUDES-OWN-GROUP",
                ThreadMetadataOverlapPolicy.TryValidateNoOverlap(overlapCandidate, 0.0, 1.0,
                    new[] { persistedOverlap }, "persisted-overlap", out persistedConflict, out persistedPolicyError),
                ref pass, ref fail, "При редактировании собственная группа исключается из проверки.");
            Check("PERSISTED-OVERLAP-POLICY-EDIT-STILL-BLOCKS-OTHER-GROUP",
                !ThreadMetadataOverlapPolicy.TryValidateNoOverlap(overlapCandidate, 0.0, 1.0,
                    new[] { persistedOverlap, CreateOverlapTestRecord("second-overlap", 0.0, 2.0, "Solid1") },
                    "persisted-overlap", out persistedConflict, out persistedPolicyError) && persistedConflict == "second-overlap",
                ref pass, ref fail, "Исключение собственной группы при редактировании не исключает конфликтующие сторонние группы.");
            Check("PERSISTED-OVERLAP-POLICY-DOES-NOT-COLLIDE-DIFFERENT-BODIES",
                ThreadMetadataOverlapPolicy.TryValidateNoOverlap(overlapCandidate, 0.0, 1.0,
                    new[] { CreateOverlapTestRecord("other-body", 0.0, 2.0, "Solid2") }, null,
                    out persistedConflict, out persistedPolicyError),
                ref pass, ref fail, "Совпадающая геометрия в другом теле многотельной детали не считается той же поверхностью.");
            ThreadMetadataRecord invalidIntervalRecord = CreateOverlapTestRecord("bad-interval", 0.0, 99.0, "Solid1");
            invalidIntervalRecord.Target.LengthCm = 1.0;
            Check("PERSISTED-OVERLAP-POLICY-FAILS-CLOSED-ON-UNKNOWN-INTERVAL",
                !ThreadMetadataOverlapPolicy.TryValidateNoOverlap(overlapCandidate, 0.0, 1.0,
                    new[] { invalidIntervalRecord }, null,
                    out persistedConflict, out persistedPolicyError) && persistedConflict == "bad-interval",
                ref pass, ref fail, "Невычислимый интервал существующей группы не превращается в разрешение на построение.");
            Check("NATIVE-COSMETIC-OVERLAP-BLOCKS", NativeThreadOverlapPolicy.ShouldBlock(true, true, 0.0, 2.0, 1.0, 3.0),
                ref pass, ref fail, "Новая физическая резьба блокируется при пересечении со штатной косметической резьбой на том же цилиндре.");
            Check("NATIVE-COSMETIC-SEPARATED-INTERVAL-ALLOWED", !NativeThreadOverlapPolicy.ShouldBlock(true, true, 0.0, 1.0, 1.01, 2.0),
                ref pass, ref fail, "Непересекающиеся осевые интервалы штатной и физической резьбы допустимы.");
            Check("NATIVE-COSMETIC-UNKNOWN-INTERVAL-FAILS-CLOSED", NativeThreadOverlapPolicy.ShouldBlock(true, false, 0.0, 0.0, 0.0, 0.0),
                ref pass, ref fail, "Если штатная косметическая резьба найдена на том же цилиндре, но её интервал неизвестен, построение блокируется.");
            Check("NATIVE-COSMETIC-TAPERED-THREAD-UNKNOWN-SUPPORT-FAILS-CLOSED",
                !NativeThreadOverlapPolicy.CanClassifyTaperedThreadAsUnrelatedToCylinder(true, true) &&
                NativeThreadOverlapPolicy.ShouldBlock(true, false, 0.0, 0.0, 0.0, 0.0),
                ref pass, ref fail, "TaperedThreadInfo описывает семейство резьбы, но не доказывает тип опорной поверхности; при недоступном ThreadedFace неизвестное пересечение с цилиндром блокируется.");
            Check("NATIVE-COSMETIC-UNKNOWN-THREAD-FAILS-CLOSED",
                !NativeThreadOverlapPolicy.CanClassifyTaperedThreadAsUnrelatedToCylinder(false, true) &&
                NativeThreadOverlapPolicy.ShouldBlock(true, false, 0.0, 0.0, 0.0, 0.0),
                ref pass, ref fail, "Неизвестный тип штатной резьбы не получает безопасное исключение и сохраняет fail-closed на том же цилиндре.");
            // Native ThreadFeature conversion must independently preflight persisted Helivanta
            // physical zones before its transaction can delete the cosmetic source feature.
            double editProposedStart, editProposedEnd, editExistingStart, editExistingEnd;
            bool editProposedIntervalKnown = NativeThreadOverlapPolicy.TryGetInterval(
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 5.0, false, 20.0, 0.0,
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, out editProposedStart, out editProposedEnd);
            bool editExistingIntervalKnown = NativeThreadOverlapPolicy.TryGetInterval(
                new[] { 0.0, 0.0, 1.0 }, new[] { 0.0, 0.0, 1.0 }, 5.0, false, 20.0, 0.0,
                new[] { 0.0, 0.0, 1.0 }, new[] { 0.0, 0.0, 1.0 }, out editExistingStart, out editExistingEnd);
            Check("NATIVE-EDIT-EXISTING-HELI-VANTA-OVERLAP-BLOCKS", editProposedIntervalKnown && editExistingIntervalKnown &&
                NativeThreadOverlapPolicy.ShouldBlock(true, true, editProposedStart, editProposedEnd, editExistingStart, editExistingEnd),
                ref pass, ref fail, "Preflight преобразования native ThreadFeature блокирует пересечение с уже сохранённой физической резьбой Helivanta на том же цилиндре до открытия транзакции.");
            Check("NATIVE-EDIT-EXISTING-HELI-VANTA-SEPARATED-ALLOWED", NativeThreadOverlapPolicy.ShouldBlock(true, true, 0.0, 1.0, 1.01, 2.0) == false,
                ref pass, ref fail, "Преобразование native ThreadFeature разрешается, если существующая физическая резьба занимает разнесённый осевой интервал.");
            Check("NATIVE-EDIT-UNKNOWN-INTERVAL-FAILS-CLOSED", NativeThreadOverlapPolicy.ShouldBlock(true, false, 0.0, 0.0, 0.0, 0.0),
                ref pass, ref fail, "Преобразование native ThreadFeature блокируется до мутации, если интервал существующей резьбы на том же цилиндре неизвестен.");
            Check("NATIVE-COSMETIC-DIFFERENT-CYLINDER-IGNORED", !NativeThreadOverlapPolicy.ShouldBlock(false, false, 0.0, 0.0, 0.0, 0.0),
                ref pass, ref fail, "Неизвестный интервал штатной резьбы на другом цилиндре не создаёт ложного конфликта.");
            double nativeStart, nativeEnd;
            Check("NATIVE-COSMETIC-INTERVAL-REVERSED-AXIS", NativeThreadOverlapPolicy.TryGetInterval(
                new[] { 0.0, 0.0, 4.0 }, new[] { 0.0, 0.0, -1.0 }, 5.0, false, 20.0, 5.0,
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, out nativeStart, out nativeEnd) &&
                Math.Abs(nativeStart - 3.5) < 1e-9 && Math.Abs(nativeEnd - 1.5) < 1e-9,
                ref pass, ref fail, "Интервал штатной резьбы вычисляется в общей системе координат при реверсированном направлении оси.");
            Check("NATIVE-COSMETIC-INTERVAL-REJECTS-OUT-OF-BOUNDS", !NativeThreadOverlapPolicy.TryGetInterval(
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 1.0, false, 20.0, 0.0,
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, out nativeStart, out nativeEnd),
                ref pass, ref fail, "Интервал штатной косметической резьбы, выходящий за границы исходного цилиндра, не принимается.");

            // Regression: the size label must come from the active standard, not a stale
            // selected item from the previous template during thread editing.
            Check("EDIT-SIZE-NAME-CURRENT-STANDARD", ThreadSizeNamePolicy.Build(
                ThreadStandard.Tr, ThreadStandards.Sizes(ThreadStandard.Tr), 45.0, 3.0) == "Tr 45 x 3",
                ref pass, ref fail, "После смены ISO M45 на Tr45×3 имя строится по стандарту Tr, а не остаётся M45×3.");
            Check("EDIT-SIZE-NAME-CUSTOM-PITCH-KEEPS-PREFIX", ThreadSizeNamePolicy.Build(
                ThreadStandard.Tr, ThreadStandards.Sizes(ThreadStandard.Tr), 45.0, 2.5) == "Tr 45 x 2.5",
                ref pass, ref fail, "Пользовательский шаг сохраняет префикс активного стандарта.");
            Check("EDIT-SIZE-NAME-RD-KEEPS-PREFIX", ThreadSizeNamePolicy.Build(
                ThreadStandard.Rd, ThreadStandards.Sizes(ThreadStandard.Rd), 45.0, 3.0) == "Rd 45 x 3",
                ref pass, ref fail, "Имя пользовательского размера Rd не наследует префикс прежнего стандарта.");
            Check("FOLDER-NAME-RIGHT-HAND-LABEL", ThreadFolderNamePolicy.DirectionLabel(true) == "Правая (R)",
                ref pass, ref fail, "Правая резьба явно обозначается в имени папки как R.");
            Check("FOLDER-NAME-LEFT-HAND-LABEL", ThreadFolderNamePolicy.DirectionLabel(false) == "Левая (L)",
                ref pass, ref fail, "Левая резьба явно обозначается в имени папки как L.");
            Check("FOLDER-NAME-BUILDER-REFLECTS-EDITED-SIZE-STARTS-AND-ISO-TEMPLATE",
                ThreadFolderNamePolicy.BuildThreadFolderName("M45 x 2", 3, true, ThreadStandard.ISO, false) ==
                "Резьба — ISO · M45 x 2 · 3 захода · Правая (R)",
                ref pass, ref fail, "Имя папки показывает шаблон ISO, новый размер и число заходов.");
            Check("FOLDER-NAME-BSW-TEMPLATE-EXPLICIT",
                ThreadFolderNamePolicy.BuildThreadFolderName("1/2-12", 1, true, ThreadStandard.BSW, false) ==
                "Резьба — BSW · 1/2-12 · 1 заход · Правая (R)",
                ref pass, ref fail, "Дюймовой размер BSW явно отличим от ANSI/UN по имени папки.");
            Check("FOLDER-NAME-ANSI-TEMPLATE-EXPLICIT",
                ThreadFolderNamePolicy.BuildThreadFolderName("1/2-13", 1, true, ThreadStandard.ANSI, false) ==
                "Резьба — ANSI/UN · 1/2-13 · 1 заход · Правая (R)",
                ref pass, ref fail, "Дюймовой размер ANSI/UN явно маркируется названием шаблона.");
            Check("FOLDER-NAME-TR-TEMPLATE-EXPLICIT-WITHOUT-DOUBLE-PREFIX",
                ThreadFolderNamePolicy.BuildThreadFolderName("Tr 45 x 3", 1, true, ThreadStandard.Tr, false) ==
                "Резьба — Tr · 45 x 3 · 1 заход · Правая (R)",
                ref pass, ref fail, "Tr обозначается по шаблону без повторения префикса в размере.");
            Check("FOLDER-NAME-S-DIN513-TEMPLATE-EXPLICIT",
                ThreadFolderNamePolicy.BuildThreadFolderName("S 45 x 2.5", 1, true, ThreadStandard.S, false) ==
                "Резьба — S (DIN 513) · 45 x 2.5 · 1 заход · Правая (R) · Прямой профиль",
                ref pass, ref fail, "Упорная резьба явно связана с шаблоном DIN 513.");
            Check("FOLDER-NAME-RD-DIN405-TEMPLATE-EXPLICIT",
                ThreadFolderNamePolicy.BuildThreadFolderName("Rd 20 x 3", 1, true, ThreadStandard.Rd, false) ==
                "Резьба — Rd (DIN 405) · 20 x 3 · 1 заход · Правая (R)",
                ref pass, ref fail, "Круглая резьба явно связана с шаблоном DIN 405.");
            Check("FOLDER-NAME-RUNOUT-EXPLICIT-TEMPLATE",
                ThreadFolderNamePolicy.BuildRunoutFolderName("1/2-12", 1, ThreadStandard.BSW) ==
                "Сбег — BSW · 1/2-12 · 1 заход",
                ref pass, ref fail, "Папка сбега также показывает семейство шаблона и правильно склоняет один заход.");
            Check("FOLDER-NAME-ENGLISH-THREAD-IS-LOCALIZED",
                ThreadFolderNamePolicy.BuildThreadFolderName("M45 x 2", 3, true, ThreadStandard.ISO, false, "en") ==
                "Thread — ISO · M45 x 2 · 3 starts · Right-hand (R)",
                ref pass, ref fail, "Новые обычные папки получают английское имя в английском интерфейсе.");
            Check("FOLDER-NAME-ENGLISH-CAP-NECK-IS-LOCALIZED",
                ThreadFolderNamePolicy.BuildPackagingThreadFolderName(22.0, 2.7, 3, true, false, "en") ==
                "Cap/neck thread — External · D22 mm · P2.7 mm · 3 starts · Right-hand (R)",
                ref pass, ref fail, "Имена папок резьбы крышек/горловин используют выбранный язык и единицы.");
            Check("FOLDER-NAME-ENGLISH-TAPERED-IS-LOCALIZED",
                ThreadFolderNamePolicy.BuildTaperedThreadFolderName("NPT", "1/2-14", 1, false, "en") ==
                "Thread — NPT · 1/2-14 · 1 start · Left-hand (L)",
                ref pass, ref fail, "Конические папки используют английские префикс и направление.");
            Check("FOLDER-POLICY-RECOGNIZES-BOTH-LANGUAGES-AND-LEGACY",
                ThreadBrowserFolderPolicy.IsPrimaryThreadFolder("Резьба — ISO · M10") &&
                ThreadBrowserFolderPolicy.IsPrimaryThreadFolder("Thread — ISO · M10") &&
                ThreadBrowserFolderPolicy.IsPackagingThreadFolder("Резьба крышек/горловин — наружная") &&
                ThreadBrowserFolderPolicy.IsPackagingThreadFolder("Cap/neck thread — External") &&
                ThreadBrowserFolderPolicy.IsPackagingThreadFolder("Упаковочная резьба — наружная") &&
                ThreadBrowserFolderPolicy.IsRunoutFolder("Сбег — ISO") &&
                ThreadBrowserFolderPolicy.IsRunoutFolder("Runout — ISO") &&
                ThreadBrowserFolderPolicy.IsManagedThreadFolder("Packaging thread — External"),
                ref pass, ref fail, "Распознавание папок централизовано и сохраняет поддержку старых русских моделей.");
            Check("FOLDER-POLICY-DETECTS-PERSISTED-LANGUAGE",
                ThreadBrowserFolderPolicy.GetLanguageForFolderName("Thread — ISO · M10", "ru") == "en" &&
                ThreadBrowserFolderPolicy.GetLanguageForFolderName("Резьба — ISO · M10", "en") == "ru" &&
                ThreadBrowserFolderPolicy.GetLanguageForFolderName("custom folder", "en") == "en",
                ref pass, ref fail, "При редактировании существующая папка сохраняет язык, а неизвестное имя использует язык интерфейса.");
            Check("CAPNECK-FOLDER-LANGUAGE-NEW-BUILD-USES-UI-AND-EDIT-PRESERVES-MODEL",
                ThreadBrowserFolderPolicy.ResolvePackagingThreadFolderNameLanguage(null, false, "en") == "en" &&
                ThreadBrowserFolderPolicy.ResolvePackagingThreadFolderNameLanguage(null, false, "ru") == "ru" &&
                ThreadBrowserFolderPolicy.ResolvePackagingThreadFolderNameLanguage("Cap/neck thread — External · D22 mm", true, "ru") == "en" &&
                ThreadBrowserFolderPolicy.ResolvePackagingThreadFolderNameLanguage("Резьба крышек/горловин — наружная · D22 мм", true, "en") == "ru" &&
                ThreadBrowserFolderPolicy.ResolvePackagingThreadFolderNameLanguage("custom folder", true, "en") == "en" &&
                ThreadBrowserFolderPolicy.NormalizeLanguage("EN-us") == "en" &&
                ThreadBrowserFolderPolicy.NormalizeLanguage("RU-ru") == "ru",
                ref pass, ref fail, "New Cap/Neck folders follow the current UI language; edit replacements preserve the language encoded by the existing model folder, with a normalized current-language fallback for custom/unknown names.");

            string runtimeAuditSourceForCapNeckLanguage = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8)
                : string.Empty;
            Check("CAPNECK-RUNTIME-AUDIT-VERIFIES-NEW-FOLDER-USES-UI-LANGUAGE",
                runtimeAuditSourceForCapNeckLanguage.Contains("expectedInitialFolderName = ThreadFolderNamePolicy.BuildPackagingThreadFolderName") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("string.Equals(initialFolder.Name, expectedInitialFolderName, StringComparison.Ordinal)") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("result.Metrics[\"InitialFolderName\"] = initialFolder.Name"),
                ref pass, ref fail,
                "The live Inventor audit must compare the actual initial root BrowserFolder.Name with the exact Cap/Neck label in the active add-in UI language and report it as a metric.");
            Check("CAPNECK-RUNTIME-AUDIT-BUILDS-AND-VERIFIES-BOTH-RU-EN-FOLDER-LANGUAGES",
                runtimeAuditSourceForCapNeckLanguage.Contains("CAPNECK-FOLDER-LOCALIZATION-RU-EN-MATRIX") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("new[] { \"en\", \"ru\" }") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("true, false, true, true, true, 2, 14.0, 1, 1.0") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("null, null, null, language, out summaryDetails") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("string.Equals(folder.Name, expectedFolderName, StringComparison.Ordinal)") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("VerifyPackagingFolderMembership(doc, group, folder") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("result.Metrics[\"FolderName_\" + language] = folder.Name"),
                ref pass, ref fail,
                "A single Inventor Runtime Audit must build independent real Cap/Neck parts with both explicit en and ru folder languages, compare exact BrowserFolder names, verify feature/metadata COM membership, and report both actual names.");
            Check("CAPNECK-RUNTIME-AUDIT-EXERCISES-LANGUAGE-CHANGE-REPEATED-EDIT-AND-UNDO",
                runtimeAuditSourceForCapNeckLanguage.Contains("persistedFolderLanguage = uiLanguage == \"en\" ? \"ru\" : \"en\";") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("P4.0→P4.2") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("P4.2→P4.4") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("UndoLastBuild(_app, doc, secondEditTransactionName)") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("FindUniqueRootPackagingFolder(doc"),
                ref pass, ref fail, "The Inventor runtime audit must simulate a model language different from the UI, re-edit Cap/Neck P4.0→P4.2→P4.4, and verify exact Undo restoration.");
            Check("CAPNECK-BROWSERTREE-REQUIRES-EXACT-NAME-AND-COM-METADATA-MEMBERSHIP",
                runtimeAuditSourceForCapNeckLanguage.Contains("VerifyPackagingFolderMembership(doc, original, initialFolder") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("VerifyPackagingFolderMembership(doc, edited, p42Folder") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("VerifyPackagingFolderMembership(doc, secondEdited, p44Folder") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("VerifyPackagingFolderMembership(doc, reverted, restoredFolder") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("StringComparison.Ordinal") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("numberedCollisionCount == 0") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("metadataGroup.MembersByIndex[memberIndex]") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("ComObjectIdentity.Compare(node, folderNode") &&
                runtimeAuditSourceForCapNeckLanguage.Contains("uniqueFolderFeatures.Count == featureMembers.Count"),
                ref pass, ref fail, "Cap/Neck build, both repeated edits and Undo must verify the exact root PartFeatureFolder name, reject #2 duplicates, and prove that every ThreadId metadata member plus every PartFeature is present inside the correct folder by COM identity.");
            Check("BROWSERTREE-STRICT-AUDIT-DOES-NOT-ACCEPT-SUBSTRING-LABELS-OR-WARN-ONLY",
                runtimeAuditSourceForCapNeckLanguage.Contains("exactRootMatches == 1 && numberedDuplicateMatches == 0") &&
                !runtimeAuditSourceForCapNeckLanguage.Contains("label.IndexOf(expectedFolder") &&
                !runtimeAuditSourceForCapNeckLanguage.Contains("result.Status = \"WARN\";\n                result.Warnings.Add(\"Построение завершено, но ожидаемое имя папки не найдено"),
                ref pass, ref fail, "The general BrowserTree audit accepts only one exact root folder name and fails closed rather than treating a descendant substring as evidence or downgrading a missing folder to WARN.");

            Check("FOLDER-NAME-STANDARD-LABELS-COVER-ALL-TEMPLATES",
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.ISO) == "ISO" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.BSW) == "BSW" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.ANSI) == "ANSI/UN" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.Tr) == "Tr" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.S) == "S (DIN 513)" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.Rd) == "Rd (DIN 405)" &&
                ThreadFolderNamePolicy.StandardLabel(ThreadStandard.GostMetric) == "ГОСТ 9150/8724",
                ref pass, ref fail, "Все семь семейств имеют устойчивые и однозначные подписи.");
            Check("UNDO-BOUNDARY-DOES-NOT-CLEAR-WITH-NO-OPEN-DOCUMENTS",
                !UndoHistoryBoundaryPolicy.ShouldClearAllTransactions(0, false),
                ref pass, ref fail, "Без открытого документа очистка глобальной истории не запускается.");
            Check("UNDO-BOUNDARY-CLEARS-ONLY-WITH-ONE-OPEN-DOCUMENT",
                UndoHistoryBoundaryPolicy.ShouldClearAllTransactions(1, false),
                ref pass, ref fail, "Граница при открытии устанавливается только при ровно одном открытом документе.");
            Check("UNDO-BOUNDARY-PRESERVES-HISTORY-WITH-TWO-OPEN-DOCUMENTS",
                !UndoHistoryBoundaryPolicy.ShouldClearAllTransactions(2, false),
                ref pass, ref fail, "При нескольких открытых документах глобальные Undo/Redo сохраняются.");
            Check("UNDO-BOUNDARY-PRESERVES-HISTORY-WITH-MANY-OPEN-DOCUMENTS",
                !UndoHistoryBoundaryPolicy.ShouldClearAllTransactions(5, false),
                ref pass, ref fail, "Защитная политика не запускает глобальный сброс при любом количестве больше одного.");
            Check("UNDO-BOUNDARY-PRESERVES-HISTORY-AFTER-MULTI-DOCUMENT-OPEN-SEQUENCE",
                !UndoHistoryBoundaryPolicy.ShouldClearAllTransactions(1, true),
                ref pass, ref fail, "Если во время связанной последовательности открытия наблюдалось несколько документов, поздний счётчик 1 не разрешает глобальный сброс.");
            Check("UNDO-BOUNDARY-RECOGNIZES-UNIDENTIFIED-TRANSACTION-SENTINEL",
                !UndoHistoryBoundaryPolicy.IsIdentifiedTransactionInProgress("Unidentified transaction"),
                ref pass, ref fail, "Постоянный sentinel Inventor не блокирует безопасную проверку границы Undo.");
            Check("UNDO-BOUNDARY-SENTINEL-CHECK-IS-CASE-INSENSITIVE",
                !UndoHistoryBoundaryPolicy.IsIdentifiedTransactionInProgress("unidentified transaction"),
                ref pass, ref fail, "Регистр имени sentinel не меняет распознавание.");
            Check("UNDO-BOUNDARY-BLOCKS-NAMED-CURRENT-TRANSACTION",
                UndoHistoryBoundaryPolicy.IsIdentifiedTransactionInProgress("Создание резьбы"),
                ref pass, ref fail, "Любая именованная текущая транзакция блокирует очистку истории.");
            Check("UNDO-BOUNDARY-FAILS-CLOSED-ON-UNKNOWN-TRANSACTION-NAME",
                UndoHistoryBoundaryPolicy.IsIdentifiedTransactionInProgress(null) &&
                UndoHistoryBoundaryPolicy.IsIdentifiedTransactionInProgress(" "),
                ref pass, ref fail, "Пустое или неизвестное имя считается активным состоянием и не разрешает сброс.");
            Check("PREVIEW-OVERLAY-BURN-THROUGH",
                PreviewGraphicsPolicy.BurnThroughModel,
                ref pass, ref fail, "Затенённый фантом и контуры рисуются поверх перекрывающего тела без изменения тела.");
            Check("PREVIEW-NEVER-CHANGES-SOURCE-BODY-VISIBILITY",
                !PreviewGraphicsPolicy.ChangesSourceBodyVisibility,
                ref pass, ref fail, "Переключение предпросмотра не изменяет SurfaceBody.Visible и не добавляет Show/Hide транзакции.");
            Check("PREVIEW-OVERLAY-USES-PRIORITY",
                PreviewGraphicsPolicy.DepthPriority > 0,
                ref pass, ref fail, "Временная графика получает положительный приоритет отображения.");
            string previewOverlaySource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadPreviewOverlay.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadPreviewOverlay.cs"), Encoding.UTF8) : string.Empty;
            string[] previewFallbackContexts =
            {
                "SetTransparencyPercent.OverrideOpacity",
                "SetLineThicknessPercent.LineWeight[",
                "ApplyColorSetOpacity.ReflectionPropertyFallback",
                "ApplyColorSetOpacity",
                "UpdateActiveView",
                "BelongsToActiveDocument",
                "BelongsToDocument",
                "Show.ChamferPreviewAssessment",
                "Show.LineStrip.LineWeight[",
                "Show.ShadedMeshNode.Selectable[",
                "Show.ShadedMeshNode.OverrideOpacity[",
                "Show.ShadedMeshNode.DeletePartialNode[",
                "Show.ActiveViewUpdate",
                "Clear.ActiveViewUpdate"
            };
            int previewClearStart = previewOverlaySource.IndexOf("public void Clear()", StringComparison.Ordinal);
            int previewClearEnd = previewOverlaySource.IndexOf("private static Groove CreateTaperedPreviewGroove", previewClearStart < 0 ? 0 : previewClearStart, StringComparison.Ordinal);
            string previewClearMethod = previewClearStart >= 0 && previewClearEnd > previewClearStart
                ? previewOverlaySource.Substring(previewClearStart, previewClearEnd - previewClearStart) : string.Empty;
            int previewClientDelete = previewClearMethod.IndexOf("_clientGraphics.Delete();", StringComparison.Ordinal);
            int previewClientReferenceClear = previewClearMethod.IndexOf("_clientGraphics = null;", StringComparison.Ordinal);
            int previewDataSetDelete = previewClearMethod.IndexOf("_dataSets.Delete();", StringComparison.Ordinal);
            Check("THREAD-PREVIEW-OPTIONAL-COM-FAILURES-ARE-DIAGNOSTIC-AND-CLEANUP-REMAINS-RETRYABLE",
                !string.IsNullOrWhiteSpace(previewOverlaySource) &&
                previewOverlaySource.Contains("HelivantaLocalization.LogSuppressedExceptionForDiagnostics(") &&
                previewOverlaySource.Contains("\"ThreadPreviewOverlay.\" + (string.IsNullOrWhiteSpace(operation)") &&
                previewFallbackContexts.All(context => previewOverlaySource.Contains("LogPreviewFallback(\"" + context)) &&
                !Regex.IsMatch(previewOverlaySource, @"catch\s*\{\s*\}") &&
                !Regex.IsMatch(previewOverlaySource, @"catch\s*\([^)]*\)\s*\{\s*\}") &&
                previewClearMethod.Contains("client-graphics-delete-failed") &&
                previewClearMethod.Contains("graphics-datasets-delete-failed") &&
                previewClearMethod.Contains("graphics-cleanup-pending") &&
                previewClientDelete >= 0 && previewClientReferenceClear > previewClientDelete &&
                previewDataSetDelete > previewClientReferenceClear &&
                previewClearMethod.Contains("LastClearSucceeded = graphicsDeleted && dataSetsDeleted && _clientGraphics == null && _dataSets == null;") &&
                previewOverlaySource.Contains("NonTransactingGraphicsDataSetsCollection.Add(GraphicsId)") &&
                previewOverlaySource.Contains("NonTransactingClientGraphicsCollection.Add(GraphicsId)"),
                ref pass, ref fail,
                "Optional preview display and ActiveView COM failures must retain diagnostic context; cleanup keeps failed COM references for retry and remains limited to non-transacting graphics.");
            Check("PREVIEW-CHAMFER-START-EDGE-FACTOR",
                ThreadPreviewChamferPolicy.IncludesStart(1) &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(0.0, 2.0, 1, 0.1) - 1.0) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(0.1, 2.0, 1, 0.1)) < 1e-10,
                ref pass, ref fail, "Фантомная фаска в начале максимальна на торце и сходится к цилиндру за заданную длину.");
            Check("PREVIEW-CHAMFER-END-EDGE-FACTOR",
                ThreadPreviewChamferPolicy.IncludesEnd(2) &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(2.0, 2.0, 2, 0.1) - 1.0) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(1.9, 2.0, 2, 0.1)) < 1e-10,
                ref pass, ref fail, "Фантомная фаска в конце привязана к дальнему торцу цилиндра.");
            Check("PREVIEW-CHAMFER-BOTH-ENDS",
                ThreadPreviewChamferPolicy.IncludesStart(3) && ThreadPreviewChamferPolicy.IncludesEnd(3) &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(0.0, 2.0, 3, 0.1) - 1.0) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(2.0, 2.0, 3, 0.1) - 1.0) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(1.0, 2.0, 3, 0.1)) < 1e-10,
                ref pass, ref fail, "Обе торцевые фаски показываются раздельно и не влияют на середину цилиндра.");
            Check("PREVIEW-CHAMFER-INVALID-VALUES-FAIL-SOFT",
                Math.Abs(ThreadPreviewChamferPolicy.DepthFactor(0.0, 2.0, 1, double.NaN)) < 1e-10 &&
                ThreadPreviewChamferPolicy.WasVisualDepthCapped(20.0, ThreadPreviewChamferPolicy.ResolveVisualDepthCm(20.0, 1.0, 2.0, 3)),
                ref pass, ref fail, "Некорректная или не помещающаяся фаска не ломает фантом; отображение ограничивается безопасным габаритом.");
            Check("PREVIEW-CHAMFER-DEPTH-TRACKS-SETTING",
                Math.Abs(ThreadPreviewChamferPolicy.ResolveVisualDepthCm(0.1, 2.0, 10.0, 3) - 0.1) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.ResolveVisualDepthCm(0.2, 2.0, 10.0, 3) - 0.2) < 1e-10 &&
                Math.Abs(ThreadPreviewChamferPolicy.ResolveVisualDepthCm(0.2, 2.0, 10.0, 3) -
                    ThreadPreviewChamferPolicy.ResolveVisualDepthCm(0.1, 2.0, 10.0, 3)) > 1e-10,
                ref pass, ref fail, "Визуальная глубина фаски меняется вместе с введённым размером, если геометрические пределы её не ограничивают.");
            Check("WALL-MINIMUM-ABSOLUTE-FLOOR",
                Math.Abs(ThreadWallThicknessPolicy.RequiredRemainingWallCm(0.03) - 0.05) < 1e-10,
                ref pass, ref fail, "Для мелкого шага остаточная стенка не может быть меньше абсолютного пола 0,5 мм.");
            Check("WALL-MINIMUM-SCALES-WITH-PITCH",
                Math.Abs(ThreadWallThicknessPolicy.RequiredRemainingWallCm(0.8) - 0.2) < 1e-10,
                ref pass, ref fail, "Для шага 8 мм минимальная остаточная стенка равна четверти шага, то есть 2 мм.");
            Check("WALL-CUT-DEPTH-UNCHANGED-WITHOUT-CHAMFER",
                Math.Abs(ThreadWallThicknessPolicy.EffectiveCutDepthCm(0.69, false, 0.1) - 0.69) < 1e-10,
                ref pass, ref fail, "Без фаски запас глубины равен глубине профиля.");
            Check("WALL-CUT-DEPTH-USES-MAX-LOCAL-CUT",
                Math.Abs(ThreadWallThicknessPolicy.EffectiveCutDepthCm(0.69, true, 0.1) - 0.69) < 1e-10,
                ref pass, ref fail, "Корневая канавка и фаска — отдельные вычитаемые объёмы; локальная радиальная глубина объединения равна максимуму, а не сумме.");
            Check("WALL-CUT-DEPTH-RESERVES-LARGER-CHAMFER",
                Math.Abs(ThreadWallThicknessPolicy.EffectiveCutDepthCm(0.69, true, 0.75) - 0.75) < 1e-10,
                ref pass, ref fail, "Если фаска глубже корня, резервируется её собственная локальная глубина.");
            double finePitchRemaining, finePitchRequired;
            Groove s52x1Groove = ThreadStandards.ComputeGroove(
                ThreadStandards.Spec(ThreadStandard.S), true, 0.1, 0.0, 0.1, 10.0);
            double s52x1EffectiveCut = s52x1Groove == null ? double.NaN :
                ThreadWallThicknessPolicy.EffectiveCutDepthCm(s52x1Groove.Depth, true, 0.1);
            Check("WALL-S52X1-WITH-1MM-CHAMFER-DOES-NOT-DOUBLE-COUNT",
                s52x1Groove != null && ThreadWallThicknessPolicy.HasSafeRemainingWall(0.35, 0.0,
                    s52x1EffectiveCut, 0.1, out finePitchRemaining, out finePitchRequired) &&
                    Math.Abs(s52x1EffectiveCut - 0.1) < 1e-8 &&
                    Math.Abs(finePitchRemaining - 0.25) < 1e-8 &&
                    Math.Abs(finePitchRequired - 0.05) < 1e-10,
                ref pass, ref fail, "Для фактической геометрии профиля S52×1 стенка 3,5 мм с фаской 1 мм учитывает максимум корня и фаски; остаток 2,5 мм безопасен по политике.");
            Check("CHAMFER-PREFLIGHT-ACCEPTS-S52X1-ONE-MM",
                ThreadWallThicknessPolicy.ValidateChamferFit(true, 2.6, 10.0, 3, 0.1, false, false) == null,
                ref pass, ref fail, "Фаска 1 мм на длинном цилиндре Ø52 проходит проверку размера; остаточная стенка оценивается отдельной политикой.");
            Check("CHAMFER-PREFLIGHT-REPORTS-SIZE-NOT-WALL",
                (ThreadWallThicknessPolicy.ValidateChamferFit(true, 0.1, 10.0, 1, 0.1, false, false) ?? "").IndexOf("радиуса", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (ThreadWallThicknessPolicy.ValidateChamferFit(true, 2.6, 0.15, 3, 0.1, false, false) ?? "").IndexOf("две фаски", StringComparison.OrdinalIgnoreCase) >= 0,
                ref pass, ref fail, "Слишком большая фаска получает конкретную причину отказа до измерения стенки.");
            Check("CHAMFER-PREFLIGHT-REPORTS-SHOULDER-CONFLICT",
                (ThreadWallThicknessPolicy.ValidateChamferFit(true, 2.6, 10.0, 1, 0.1, true, false) ?? "").IndexOf("вогнутом", StringComparison.OrdinalIgnoreCase) >= 0 &&
                ThreadWallThicknessPolicy.ValidateChamferFit(true, 2.6, 10.0, 1, 0.1, false, false) == null,
                ref pass, ref fail, "Конфликт фаски с плечом сообщается отдельно от толщины стенки.");
            HelivantaPolicyFailure typedChamferFailure = ThreadWallThicknessPolicy.GetChamferFitFailure(
                true, 0.1, 10.0, 1, 0.1, false, false);
            string typedChamferEnglish = typedChamferFailure == null || englishCatalog == null ? null :
                englishCatalog.GetString(typedChamferFailure.ResourceKey, CultureInfo.InvariantCulture);
            string typedChamferEnglishFormatted = string.IsNullOrWhiteSpace(typedChamferEnglish) || typedChamferFailure == null ? null :
                string.Format(CultureInfo.GetCultureInfo("en-US"), typedChamferEnglish, typedChamferFailure.FormatArgs);
            Check("WALL-CHAMFER-TYPED-FAILURE-USES-STABLE-CODE-RESOURCE-AND-ARGS",
                typedChamferFailure != null && typedChamferFailure.Code == "WallChamfer.ExceedsRadius" &&
                typedChamferFailure.ResourceKey == "ThreadWallThickness.Chamfer.DepthExceedsRadius" &&
                typedChamferFailure.FormatArgs.Length == 1 && Convert.ToDouble(typedChamferFailure.FormatArgs[0], CultureInfo.InvariantCulture) == 1.0 &&
                typedChamferEnglishFormatted != null && typedChamferEnglishFormatted.Contains("1 mm chamfer") &&
                !typedChamferEnglishFormatted.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail, "Chamfer-fit failures expose stable identity and English rendering receives numeric arguments, not a prebuilt Russian sentence.");
            string[] chamferFailureKeys = {
                "ThreadWallThickness.Chamfer.UnknownMode", "ThreadWallThickness.Chamfer.DepthMustBePositive",
                "ThreadWallThickness.Chamfer.SurfaceLengthUnavailable", "ThreadWallThickness.Chamfer.DepthExceedsRadius",
                "ThreadWallThickness.Chamfer.DepthExceedsLength", "ThreadWallThickness.Chamfer.BothEndsDoNotFit",
                "ThreadWallThickness.Chamfer.StartShoulderConflict", "ThreadWallThickness.Chamfer.EndShoulderConflict" };
            Check("WALL-CHAMFER-TYPED-FAILURE-RESOURCES-HAVE-RU-EN-PARITY",
                russianCatalog != null && englishCatalog != null && chamferFailureKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !englishCatalog.GetString(key, CultureInfo.InvariantCulture).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))),
                ref pass, ref fail, "All chamfer-fit failure reasons resolve to explicit Russian and English resources.");
            Check("WALL-CUT-DEPTH-FAILS-CLOSED-ON-INVALID-CHAMFER",
                double.IsNaN(ThreadWallThicknessPolicy.EffectiveCutDepthCm(0.69, true, 0.0)) &&
                double.IsNaN(ThreadWallThicknessPolicy.EffectiveCutDepthCm(double.NaN, false, 0.0)),
                ref pass, ref fail, "Некорректная глубина профиля/включённой фаски не разрешает построение.");

            double wallRemaining, wallRequired;
            Check("WALL-POLICY-SAFE-RESIDUAL",
                ThreadWallThicknessPolicy.HasSafeRemainingWall(0.8, 0.0, 0.55, 0.8,
                    out wallRemaining, out wallRequired) &&
                Math.Abs(wallRemaining - 0.25) < 1e-10 && Math.Abs(wallRequired - 0.2) < 1e-10,
                ref pass, ref fail, "Построение разрешено, когда после корня резьбы остаётся достаточная стенка.");
            Check("WALL-POLICY-BLOCKS-THIN-RESIDUAL",
                !ThreadWallThicknessPolicy.HasSafeRemainingWall(0.7, 0.0, 0.55, 0.8,
                    out wallRemaining, out wallRequired) && Math.Abs(wallRemaining - 0.15) < 1e-10,
                ref pass, ref fail, "Построение блокируется, если остаточная стенка меньше четверти шага.");

            Check("WALL-EXTERNAL-ADDED-RADIUS-INCREASES-RESIDUAL",
                Math.Abs(ThreadWallThicknessPolicy.RadiusAdjustmentCm(true, 2.60, 2.62) - 0.02) < 1e-10 &&
                ThreadWallThicknessPolicy.HasSafeRemainingWall(0.8,
                    ThreadWallThicknessPolicy.RadiusAdjustmentCm(true, 2.60, 2.62), 0.6, 0.8,
                    out wallRemaining, out wallRequired),
                ref pass, ref fail, "Увеличение наружного радиуса добавляет припуск, учтённый в остаточной стенке.");
            Check("WALL-EXTERNAL-REMOVED-RADIUS-REDUCES-RESIDUAL",
                Math.Abs(ThreadWallThicknessPolicy.RadiusAdjustmentCm(true, 2.62, 2.60) + 0.02) < 1e-10 &&
                !ThreadWallThicknessPolicy.HasSafeRemainingWall(0.8,
                    ThreadWallThicknessPolicy.RadiusAdjustmentCm(true, 2.62, 2.60), 0.6, 0.8,
                    out wallRemaining, out wallRequired),
                ref pass, ref fail, "Приведение завышенного наружного диаметра к расчётному уменьшает остаточную стенку.");
            Check("WALL-INTERNAL-REDUCED-BORE-ADDS-WALL",
                Math.Abs(ThreadWallThicknessPolicy.RadiusAdjustmentCm(false, 1.0, 0.98) - 0.02) < 1e-10 &&
                ThreadWallThicknessPolicy.HasSafeRemainingWall(0.8,
                    ThreadWallThicknessPolicy.RadiusAdjustmentCm(false, 1.0, 0.98), 0.6, 0.8,
                    out wallRemaining, out wallRequired),
                ref pass, ref fail, "Уменьшение исходного отверстия добавляет материал и учитывается при проверке внутренней резьбы.");
            Check("WALL-INTERNAL-ENLARGED-BORE-REMOVES-WALL",
                Math.Abs(ThreadWallThicknessPolicy.RadiusAdjustmentCm(false, 0.98, 1.0) + 0.02) < 1e-10 &&
                !ThreadWallThicknessPolicy.HasSafeRemainingWall(0.8,
                    ThreadWallThicknessPolicy.RadiusAdjustmentCm(false, 0.98, 1.0), 0.6, 0.8,
                    out wallRemaining, out wallRequired),
                ref pass, ref fail, "Увеличение исходного отверстия уменьшает остаточную стенку и может блокировать построение.");
            Check("WALL-POLICY-FAILS-CLOSED-ON-NONFINITE",
                !ThreadWallThicknessPolicy.HasSafeRemainingWall(double.NaN, 0.0, 0.3, 0.8,
                    out wallRemaining, out wallRequired) && double.IsNaN(wallRemaining),
                ref pass, ref fail, "Отсутствующее или некорректное измерение стенки блокирует построение.");
            Check("TAPER-WALL-STRICT-STANDARD-DIAMETER-MISMATCH-EXPLANATION",
                ThreadWallThicknessPolicy.ShouldExplainStandardDiameterMismatch(true, true, -9.3652, 0.1479) &&
                !ThreadWallThicknessPolicy.ShouldExplainStandardDiameterMismatch(true, false, -9.3652, 0.1479) &&
                !ThreadWallThicknessPolicy.ShouldExplainStandardDiameterMismatch(false, true, -9.3652, 0.1479) &&
                !ThreadWallThicknessPolicy.ShouldExplainStandardDiameterMismatch(true, true, -0.1, 0.1479) &&
                !ThreadWallThicknessPolicy.ShouldExplainStandardDiameterMismatch(true, true, double.NaN, 0.1479),
                ref pass, ref fail, "Большое уменьшение наружного радиуса в строгом режиме меняет только пояснение отказа, но не критерий безопасности.");
            Check("TAPER-WALL-JIS-R6-350MM-25MM-STOCK-REMAINS-FAIL-CLOSED",
                !ThreadWallThicknessPolicy.HasSafeRemainingWall(2.5, -9.3652, 0.1479, 0.2309,
                    out wallRemaining, out wallRequired) &&
                Math.Abs(wallRemaining * 10.0 + 70.131) < 1e-8 &&
                Math.Abs(wallRequired * 10.0 - 0.57725) < 1e-8,
                ref pass, ref fail, "Расчёт JIS R6 на Ø350 с измеренной стенкой 25 мм остаётся безопасно заблокированным: остаток −70,131 мм при требовании 0,57725 мм.");
            object[] taperedDiameterMismatchArgs = { "JIS Taper", "R 6", 350.0, 162.696, 165.2, 93.652, 25.0, 1.479, -70.131, 0.577 };
            string ruTaperedDiameterMismatch = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadWallThickness.Error.TaperedStandardDiameterMismatch", CultureInfo.InvariantCulture);
            string enTaperedDiameterMismatch = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadWallThickness.Error.TaperedStandardDiameterMismatch", CultureInfo.InvariantCulture);
            string formattedEnTaperedDiameterMismatch = string.IsNullOrWhiteSpace(enTaperedDiameterMismatch) ? null :
                string.Format(CultureInfo.GetCultureInfo("en-US"), enTaperedDiameterMismatch, taperedDiameterMismatchArgs);
            Check("TAPER-WALL-STRICT-STANDARD-MISMATCH-RU-EN-RESOURCES",
                !string.IsNullOrWhiteSpace(ruTaperedDiameterMismatch) &&
                !string.IsNullOrWhiteSpace(enTaperedDiameterMismatch) &&
                !enTaperedDiameterMismatch.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                formattedEnTaperedDiameterMismatch != null && formattedEnTaperedDiameterMismatch.Contains("Ø350 mm") &&
                formattedEnTaperedDiameterMismatch.Contains("Ø162.696 mm") && formattedEnTaperedDiameterMismatch.Contains("-70.131 mm"),
                ref pass, ref fail, "RU/EN сообщение отдельно объясняет несовместимость табличного диаметра, не меняя отказ по толщине стенки.");
            ThreadTargetSnapshot sourceCylinder = new ThreadTargetSnapshot
            {
                Center = new[] { 0.0, 0.0, 0.0 },
                Axis = new[] { 0.0, 0.0, 1.0 },
                RadiusCm = 0.5,
                LengthCm = 2.0,
                IsExternal = true,
                BodyName = "Solid1"
            };
            ThreadTargetSnapshot sameBodyAxialSegment = CloneTarget(sourceCylinder);
            sameBodyAxialSegment.Center = new[] { 0.0, 0.0, 2.0 };
            Check("TARGET-ISOLATION-SAME-BODY-AXIAL-SEGMENTS",
                ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, sameBodyAxialSegment) &&
                !ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 1.0, 1.1, 2.0),
                ref pass, ref fail, "Разнесённые осевые интервалы одного цилиндра распознаются как один источник, но не как перекрытие.");

            ThreadTargetSnapshot coincidentOtherBody = CloneTarget(sourceCylinder);
            coincidentOtherBody.BodyName = "Solid2";
            Check("TARGET-ISOLATION-SEPARATE-BODIES",
                !ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, coincidentOtherBody),
                ref pass, ref fail, "Геометрически совпадающие цилиндры в разных телах многотельной детали изолированы.");

            ThreadTargetSnapshot radialNeighbor = CloneTarget(sourceCylinder);
            radialNeighbor.Center = new[] { 0.02, 0.0, 0.0 };
            Check("TARGET-ISOLATION-RADIAL-NEIGHBOR",
                !ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, radialNeighbor),
                ref pass, ref fail, "Параллельный цилиндр со смещённой осью не смешивается с исходной поверхностью.");

            ThreadTargetSnapshot differentRadius = CloneTarget(sourceCylinder);
            differentRadius.RadiusCm = 0.6;
            Check("TARGET-ISOLATION-RADIUS-MISMATCH",
                !ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, differentRadius),
                ref pass, ref fail, "Цилиндры с различными радиусами не считаются одним источником.");

            ThreadTargetSnapshot reversedAxis = CloneTarget(sourceCylinder);
            reversedAxis.Center = new[] { 0.0, 0.0, 1.0 };
            reversedAxis.Axis = new[] { 0.0, 0.0, -1.0 };
            Check("TARGET-ISOLATION-REVERSED-AXIS",
                ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, reversedAxis),
                ref pass, ref fail, "Разворот направления оси не ломает идентификацию того же цилиндра.");

            ThreadTargetSnapshot caseVariantBody = CloneTarget(sourceCylinder);
            caseVariantBody.BodyName = "solid1";
            Check("TARGET-ISOLATION-BODY-NAME-CASE-INSENSITIVE",
                ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, caseVariantBody),
                ref pass, ref fail, "Имя одного тела сравнивается без учёта регистра.");

            Check("TARGET-ISOLATION-OVERLAPPING-THREAD-RANGES",
                ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, sameBodyAxialSegment) &&
                ThreadEditSafetyPolicy.IntervalsOverlap(0.0, 1.0, 0.8, 1.5),
                ref pass, ref fail, "Перекрывающиеся интервалы на одном цилиндре остаются конфликтом.");

            ThreadTargetSnapshot invalidTarget = CloneTarget(sourceCylinder);
            invalidTarget.Axis = null;
            Check("TARGET-ISOLATION-TRANSIENT-MISSING-BODY-NAME",
                ThreadTargetIsolationPolicy.SameCylinderGeometry(
                    "Solid1", null, 0.5, 0.5,
                    sourceCylinder.Center, sourceCylinder.Axis,
                    sourceCylinder.Center, sourceCylinder.Axis, requireBodyNames: false),
                ref pass, ref fail, "Для временного выбора сохраняется прежнее поведение, если имя тела недоступно.");

            Check("TARGET-ISOLATION-INVALID-METADATA-FAILS-CLOSED",
                !ThreadTargetIsolationPolicy.SameCylinder(sourceCylinder, invalidTarget),
                ref pass, ref fail, "Повреждённые геометрические снимки не разрешают небезопасное совпадение.");
            var existingPackagingRecord = new ThreadMetadataRecord
            {
                SchemaVersion = 2,
                Family = ThreadMetadataRecord.CapNeckFamily,
                ThreadId = "existing-capneck-thread",
                ProducerVersion = "test",
                ObjectCount = 1,
                PackagingParameters = new PackagingThreadParametersSnapshot
                {
                    DiameterMm = 350.0,
                    PitchMm = 10.5,
                    Starts = 1,
                    HeightRatio = 0.02,
                    ClearanceMm = 0.0,
                    RightHand = true,
                    InternalThread = false,
                    LengthInputValue = 25.0,
                    LengthAsTurns = false,
                    ResolvedAxialLengthMm = 15.0,
                    ResolvedStartOffsetMm = 10.0,
                    MeasuredWallMm = 10.0,
                    AutoPitchEnabled = false,
                    SmoothStart = false,
                    SmoothEnd = false,
                    VentSlotsEnabled = false,
                    VentSlotCount = 2,
                    VentSlotWidthDegrees = 14.0,
                    ChamferMode = 0,
                    ChamferMm = 0.0
                },
                Target = new ThreadTargetSnapshot
                {
                    Center = new[] { 0.0, 0.0, 7.0 },
                    Axis = new[] { 0.0, 0.0, -1.0 },
                    RadiusCm = 17.5,
                    LengthCm = 7.0,
                    IsExternal = true,
                    BodyName = "Solid1"
                }
            };
            var proposedOppositeEnd = new ThreadTargetSnapshot
            {
                Center = new[] { 0.0, 0.0, 0.0 },
                Axis = new[] { 0.0, 0.0, 1.0 },
                RadiusCm = 17.5,
                LengthCm = 7.0,
                IsExternal = true,
                BodyName = "Solid1"
            };
            var existingStandardRecord = new ThreadMetadataRecord
            {
                SchemaVersion = 1,
                ThreadId = "existing-standard-thread",
                ProducerVersion = "test",
                ObjectCount = 1,
                Parameters = new ThreadParametersSnapshot
                {
                    Standard = 0, SizeName = "test-thread", PitchMm = 10.5, NominalDiaMm = 350.0,
                    Starts = 1, ClearanceMm = 0.0, External = true, Round = false, Smooth = false,
                    SimplifiedRunout = false, RunSteps = 0.0, Mirror = false, SortThreadsIntoFolders = false,
                    FullLength = false, LengthMm = 15.0, OffsetMm = 10.0, RunOut = false, RightHand = true,
                    ChamferMode = 0, ChamferMm = 0.0, ToleranceClass = string.Empty, Tapered = false,
                    TaperGeometryMode = 0
                },
                Target = CloneTarget(existingPackagingRecord.Target)
            };
            var sameCylinderDifferentBodyRecord = CloneRecord(existingPackagingRecord);
            sameCylinderDifferentBodyRecord.ThreadId = "same-geometry-other-body";
            sameCylinderDifferentBodyRecord.Target.BodyName = "Solid2";
            PackagingThreadOverlapResult standardThreadOverlap = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 50.0, new[] { existingStandardRecord });
            PackagingThreadOverlapResult differentBody = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 50.0, new[] { sameCylinderDifferentBodyRecord });
            PackagingThreadOverlapResult oppositeEndOverlap = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 50.0, new[] { existingPackagingRecord });
            PackagingThreadOverlapResult oppositeEndSeparated = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 30.0, new[] { existingPackagingRecord });
            PackagingThreadOverlapResult ignoredReplacement = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 50.0, new[] { existingPackagingRecord }, "existing-capneck-thread");
            PackagingThreadOverlapResult unavailableMetadata = PackagingThreadOverlapPolicy.Evaluate(
                proposedOppositeEnd, 10.0, 50.0, new ThreadMetadataRecord[] { null });

            // Regression: after the first thread is built, Inventor may split the opposite-end
            // cylinder into a shorter face fragment. The requested interval must not be clipped
            // to that fragment before overlap checking (e.g. cylinder 40 mm, existing 25 mm,
            // requested 25 or 60 mm from the other end).
            var persistedTwentyFiveMmThread = CloneRecord(existingPackagingRecord);
            persistedTwentyFiveMmThread.ThreadId = "existing-25mm-opposite-end";
            persistedTwentyFiveMmThread.PackagingParameters.DiameterMm = 31.0;
            persistedTwentyFiveMmThread.PackagingParameters.PitchMm = 2.7;
            persistedTwentyFiveMmThread.PackagingParameters.LengthInputValue = 25.0;
            persistedTwentyFiveMmThread.PackagingParameters.ResolvedStartOffsetMm = 2.025;
            persistedTwentyFiveMmThread.PackagingParameters.ResolvedAxialLengthMm = 25.0;
            persistedTwentyFiveMmThread.Target.Center = new[] { 0.0, 0.0, 4.0 };
            persistedTwentyFiveMmThread.Target.Axis = new[] { 0.0, 0.0, -1.0 };
            persistedTwentyFiveMmThread.Target.RadiusCm = 1.55;
            persistedTwentyFiveMmThread.Target.LengthCm = 4.0;
            ThreadTargetSnapshot splitFaceOppositeEnd = CloneTarget(proposedOppositeEnd);
            splitFaceOppositeEnd.RadiusCm = 1.55;
            splitFaceOppositeEnd.LengthCm = 2.0675; // surface fragment left after the first thread
            PackagingThreadOverlapResult requested25Overlaps = PackagingThreadOverlapPolicy.Evaluate(
                splitFaceOppositeEnd, 2.025, 25.0, new[] { persistedTwentyFiveMmThread });
            PackagingThreadOverlapResult requested60Overlaps = PackagingThreadOverlapPolicy.Evaluate(
                splitFaceOppositeEnd, 2.025, 60.0, new[] { persistedTwentyFiveMmThread });
            PackagingThreadOverlapResult requested10Separated = PackagingThreadOverlapPolicy.Evaluate(
                splitFaceOppositeEnd, 2.025, 10.0, new[] { persistedTwentyFiveMmThread });
            Check("PACKAGING-OVERLAP-USES-REQUESTED-LENGTH-NOT-SPLIT-FACE-CLAMP",
                persistedTwentyFiveMmThread.IsValid() &&
                requested25Overlaps.Status == PackagingThreadOverlapStatus.Conflict &&
                Math.Abs(requested25Overlaps.OverlapLengthMm - 14.05) < 1e-6 &&
                requested60Overlaps.Status == PackagingThreadOverlapStatus.Conflict &&
                Math.Abs(requested60Overlaps.OverlapLengthMm - 25.0) < 1e-6 &&
                requested10Separated.Status == PackagingThreadOverlapStatus.Clear,
                ref pass, ref fail,
                "On a 40 mm cylinder with an existing 25 mm thread at the opposite end, requests of 25 mm and 60 mm are checked at their full requested interval even if Inventor exposes only a 20.675 mm face fragment; a separated 10 mm interval remains allowed.");

            Check("PACKAGING-NEW-BUILD-OVERLAP-PREFLIGHT-OPPOSITE-ENDS-AND-SEPARATED-RANGES",
                existingPackagingRecord.IsValid() &&
                oppositeEndOverlap.Status == PackagingThreadOverlapStatus.Conflict &&
                Math.Abs(oppositeEndOverlap.ExistingDiameterMm - 350.0) < 1e-8 &&
                Math.Abs(oppositeEndOverlap.ExistingPitchMm - 10.5) < 1e-8 &&
                Math.Abs(oppositeEndOverlap.OverlapLengthMm - 15.0) < 1e-8 &&
                oppositeEndSeparated.Status == PackagingThreadOverlapStatus.Clear &&
                standardThreadOverlap.Status == PackagingThreadOverlapStatus.Conflict &&
                existingStandardRecord.IsValid() &&
                differentBody.Status == PackagingThreadOverlapStatus.Clear &&
                ignoredReplacement.Status == PackagingThreadOverlapStatus.Clear &&
                unavailableMetadata.Status == PackagingThreadOverlapStatus.Unverifiable,
                ref pass, ref fail,
                "New Cap/Neck threads started at the opposite end of the same cylinder are blocked only when persisted axial intervals overlap; separated ranges and a replacement's own ThreadId remain allowed, while unavailable metadata fails closed.");

            Check("FOLDER-NAME-UNKNOWN-TEMPLATE-DOES-NOT-MISLABEL-ISO",
                ThreadFolderNamePolicy.StandardLabel((ThreadStandard)999) == "Неизвестный шаблон",
                ref pass, ref fail, "Некорректное значение стандарта не маскируется подписью ISO.");
            Check("FOLDER-NAME-S-STRAIGHT-PROFILE", ThreadFolderNamePolicy.AppendThreadOrientation(
                "Резьба — S 45 x 2.5 · 1 заход", true, ThreadStandard.S, false) ==
                "Резьба — S 45 x 2.5 · 1 заход · Правая (R) · Прямой профиль",
                ref pass, ref fail, "Для S в имени папки показано правое направление и прямой профиль.");
            Check("FOLDER-NAME-S-MIRRORED-PROFILE", ThreadFolderNamePolicy.AppendThreadOrientation(
                "Резьба — S 45 x 2.5 · 1 заход", false, ThreadStandard.S, true) ==
                "Резьба — S 45 x 2.5 · 1 заход · Левая (L) · Зеркальный профиль",
                ref pass, ref fail, "Для S в имени папки показаны левое направление и зеркальный профиль.");
            Check("FOLDER-NAME-NON-S-NO-PROFILE-LABEL", ThreadFolderNamePolicy.AppendThreadOrientation(
                "Резьба — Tr 45 x 3 · 1 заход", true, ThreadStandard.Tr, true) ==
                "Резьба — Tr 45 x 3 · 1 заход · Правая (R)",
                ref pass, ref fail, "Метка прямого/зеркального профиля не добавляется к стандартам, отличным от S.");

            Check("EDIT-CLEANUP-INCLUDES-RUNOUT-AND-PACKAGING-THREAD-FOLDERS",
                ThreadBrowserFolderPolicy.IsManagedThreadFolder("Сбег — Tr 45 x 3 · 1 заход") &&
                ThreadBrowserFolderPolicy.IsManagedThreadFolder("Резьба — Tr 45 x 3 · 1 заход") &&
                ThreadBrowserFolderPolicy.IsManagedThreadFolder("Резьба крышек/горловин — наружная · D22 мм · P2.7 мм · 3 захода · Правая (R)") &&
                ThreadBrowserFolderPolicy.IsManagedThreadFolder("Упаковочная резьба — наружная · D22 мм · P2.7 мм · 3 захода · Правая (R)") &&
                !ThreadBrowserFolderPolicy.IsManagedThreadFolder("Эскизы"),
                ref pass, ref fail, "Очистка рассматривает пустые папки обычной резьбы, сбега и резьбы крышек/горловин (в том числе старые имена), не затрагивая чужие папки.");

            string runoutFolderNestingSource = threadBuilderSourceForTransactions;
            int sharedFolderBuildStart = runoutFolderNestingSource.IndexOf(
                "// Run-out operations are part of the same logical thread as the main Coil.", StringComparison.Ordinal);
            int sharedFolderBuildEnd = runoutFolderNestingSource.IndexOf(
                "string coreTiming = string.Format(", sharedFolderBuildStart < 0 ? 0 : sharedFolderBuildStart, StringComparison.Ordinal);
            string sharedFolderBuildBlock = sharedFolderBuildStart >= 0 && sharedFolderBuildEnd > sharedFolderBuildStart
                ? runoutFolderNestingSource.Substring(sharedFolderBuildStart, sharedFolderBuildEnd - sharedFolderBuildStart)
                : string.Empty;
            string runtimeAuditSourceForRunout = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8)
                : string.Empty;
            string threadIntegrityCheckerSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadIntegrityChecker.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadIntegrityChecker.cs"), Encoding.UTF8)
                : string.Empty;
            string metadataRecordSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadMetadataRecord.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadMetadataRecord.cs"), Encoding.UTF8)
                : string.Empty;
            string metadataTestSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "..", "tests", "ThreadMetadataTest", "Program.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "..", "tests", "ThreadMetadataTest", "Program.cs"), Encoding.UTF8)
                : string.Empty;
            string threadIntegrityDialogSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadIntegrityDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadIntegrityDialog.cs"), Encoding.UTF8)
                : string.Empty;

            Check("RUNOUT-OPERATIONS-STAY-IN-ONE-SHARED-THREAD-FOLDER",
                !runoutFolderNestingSource.Contains("NestRunoutFolderUnderThreadFolder") &&
                !runoutFolderNestingSource.Contains("BuildRunoutFolderName(p.SizeName") &&
                !runoutFolderNestingSource.Contains("runoutCreated") &&
                sharedFolderBuildBlock.Contains("if (p.SortThreadsIntoFolders)") &&
                sharedFolderBuildBlock.Contains("CreateThreadBrowserFolder(") &&
                sharedFolderBuildBlock.Contains("browserAnchor, created, p, n)") &&
                sharedFolderBuildBlock.Contains("separateRunoutFolder=false") &&
                !sharedFolderBuildBlock.Contains("CreateNamedBrowserFolder(") &&
                !sharedFolderBuildBlock.Contains("Expanded = false"),
                ref pass, ref fail, "Main thread geometry, run-out features and their helpers remain in the one logical thread folder; no run-out BrowserFolder is created, no folder-collapse COM call is made, and the existing sorting preference is respected.");

            int loftVisibilityStart = runoutFolderNestingSource.IndexOf("// Work planes were hidden at creation.", StringComparison.Ordinal);
            int loftVisibilityEnd = runoutFolderNestingSource.IndexOf("runoutTimer.Stop();", loftVisibilityStart < 0 ? 0 : loftVisibilityStart, StringComparison.Ordinal);
            string loftVisibilityBlock = loftVisibilityStart >= 0 && loftVisibilityEnd > loftVisibilityStart
                ? runoutFolderNestingSource.Substring(loftVisibilityStart, loftVisibilityEnd - loftVisibilityStart)
                : string.Empty;
            Check("SEQUENTIAL-GOST-MULTI-GROUP-AUDIT-EXPECTS-TWO-SOLID-BODIES",
                runtimeAuditSourceForRunout.Contains("VerifyModelMetrics(doc, out ModelMetrics beforeFirstEdit, expectedBodyCount: 2);") &&
                runtimeAuditSourceForRunout.Contains("VerifyModelMetrics(doc, out ModelMetrics afterFirstEdit, expectedBodyCount: 2);") &&
                runtimeAuditSourceForRunout.Contains("VerifyModelMetrics(doc, out ModelMetrics beforeSecondEdit, expectedBodyCount: 2);") &&
                runtimeAuditSourceForRunout.Contains("VerifyModelMetrics(doc, out ModelMetrics afterSecondEdit, expectedBodyCount: 2);") &&
                runtimeAuditSourceForRunout.Contains("Require(bodies == expectedBodyCount"),
                ref pass, ref fail, "The sequential GOST fixture intentionally uses two independent solid bodies, so both edit fingerprints must validate a body count of two; single-body scenarios keep the default expectation of one.");

            Check("RUNTIME-AUDIT-COVERS-GOST-TR-S-CATALOGUE-ISOLATION",
                runtimeAuditSourceForRunout.Contains("STANDARD-GOST-TR-S-CATALOGUE-ISOLATION") &&
                runtimeAuditSourceForRunout.Contains("gostTr.Count == 222 && dinTr.Count == 378") &&
                runtimeAuditSourceForRunout.Contains("gostS.Count == 232 && dinS.Count == 62") &&
                runtimeAuditSourceForRunout.Contains("HasCataloguePair(gostTr, 640.0, 24.0)") &&
                runtimeAuditSourceForRunout.Contains("HasCataloguePair(dinTr, 310.0, 5.0)") &&
                runtimeAuditSourceForRunout.Contains("HasCataloguePair(gostS, 640.0, 24.0)") &&
                runtimeAuditSourceForRunout.Contains("HasCataloguePair(dinS, 105.0, 12.0)") &&
                runtimeAuditSourceForRunout.Contains("(int)ThreadStandard.GostTrapezoidal == 7") &&
                runtimeAuditSourceForRunout.Contains("(int)ThreadStandard.GostButtress == 8"),
                ref pass, ref fail, "The runtime audit checks stable standard IDs, normative catalogue counts, shared pairs and directional ГОСТ-only/DIN-only sentinels for both new families.");

            Check("RUNTIME-AUDIT-COVERS-GOST-TR-AND-GOST-S-EXTERNAL-ROLLBACK-EDIT-UNDO",
                runtimeAuditSourceForRunout.Contains("bool includeEditAndRollback = true;") &&
                runtimeAuditSourceForRunout.Contains("STANDARD-EXTERNAL-") &&
                runtimeAuditSourceForRunout.Contains("RunStandardEditRollbackChecks(doc, group, parameters, result)") &&
                runtimeAuditSourceForRunout.Contains("ThreadStandards.GostTrapezoidalDesignation(") &&
                runtimeAuditSourceForRunout.Contains("replacement.Starts = originalParameters.Starts == 2 ? 3 : 2;") &&
                runtimeAuditSourceForRunout.Contains("replacement.PitchMm = Math.Abs(originalParameters.PitchMm - 3.0) < 1e-8 ? 6.0 : 3.0;"),
                ref pass, ref fail, "Both new ГОСТ families run on their own catalogue fixtures; the external runtime sweep includes rejected-edit rollback, valid family-specific replacement, B-rep change and Undo.");

            int standardMatrixStart = runtimeAuditSourceForRunout.IndexOf("private void RunStandardDiameterBoundaryMatrixCase", StringComparison.Ordinal);
            int standardMatrixEnd = standardMatrixStart < 0 ? -1 : runtimeAuditSourceForRunout.IndexOf("private void RunStandardEditRollbackChecks", standardMatrixStart, StringComparison.Ordinal);
            string standardMatrixSource = standardMatrixStart >= 0 && standardMatrixEnd > standardMatrixStart
                ? runtimeAuditSourceForRunout.Substring(standardMatrixStart, standardMatrixEnd - standardMatrixStart)
                : string.Empty;
            bool everyCylindricalFamilyHasIndependentFiveFixtureMatrix =
                runtimeAuditSourceForRunout.Contains("if (current == ThreadStandard.GostPipeTapered) continue;") &&
                runtimeAuditSourceForRunout.Contains("bool includeEditAndRollback = true;") &&
                runtimeAuditSourceForRunout.Contains("RunCase(report, \"STANDARD-EXTERNAL-\" + current.ToString().ToUpperInvariant()") &&
                runtimeAuditSourceForRunout.Contains("RunCase(report, \"STANDARD-DIAMETER-BOUNDARY-MATRIX-\" + current.ToString().ToUpperInvariant()") &&
                standardMatrixSource.Contains("new { Key = \"standard-min\"") &&
                standardMatrixSource.Contains("new { Key = \"standard-max\"") &&
                standardMatrixSource.Contains("new { Key = \"between-rows\"") &&
                standardMatrixSource.Contains("new { Key = \"below-range\"") &&
                standardMatrixSource.Contains("high.DiameterMm * 1.25") &&
                standardMatrixSource.Contains("ThreadSize middleCatalogueRow = rows[rows.Count / 2];") &&
                standardMatrixSource.Contains("isGostTrapezoidal ? middleCatalogueRow.DiameterMm : middleDiameter") &&
                standardMatrixSource.Contains("isGostTrapezoidal ? middleCatalogueRow : middlePitchRow") &&
                standardMatrixSource.Contains("foreach (var fixture in fixtures)") &&
                standardMatrixSource.Contains("PartDocument doc = null;") &&
                standardMatrixSource.Contains("double fixtureLengthMm = Math.Max(15.0, fixture.PitchRow.PitchMm * 3.0);") &&
                standardMatrixSource.Contains("double stockLengthMm = Math.Max(45.0, fixtureOffsetMm + fixtureLengthMm + 10.0);") &&
                standardMatrixSource.Contains("fixture.Key + \".ThreadLengthMm\"") &&
                standardMatrixSource.Contains("bool expectWallTooThin =") &&
                standardMatrixSource.Contains("fixture.Key == \"standard-min\" || fixture.Key == \"below-range\"") &&
                standardMatrixSource.Contains("expectGostTrCatalogueRejection") &&
                standardMatrixSource.Contains("fixture.Key == \"far-above-range\"") &&
                standardMatrixSource.Contains("ГОСТ 24739-81") &&
                standardMatrixSource.Contains("VerifyExpectedMatrixPolicyRejection") &&
                standardMatrixSource.Contains("HelivantaErrorCode.WallThicknessPreflightRejected") &&
                standardMatrixSource.Contains("exception.DiagnosticDetail.StartsWith(\"WallTooThin;\"") &&
                standardMatrixSource.Contains("ModelSnapshotComparison") &&
                standardMatrixSource.Contains("ModelUnchangedAfterPolicyReject") &&
                standardMatrixSource.Contains("CloseTestDocument(doc, result);") &&
                standardMatrixSource.Contains("Require(completed == fixtures.Length") &&
                runtimeAuditSourceForRunout.Contains("replacement.PitchMm * 3.0") &&
                runtimeAuditSourceForRunout.Contains("AppliedReplacementLengthMm") &&
                runtimeAuditSourceForRunout.Contains("RunStandardEditRollbackChecks(doc, group, parameters, result)");
            Check("RUNTIME-AUDIT-COVERS-FIVE-DIAMETER-MATRIX-FOR-EVERY-CYLINDRICAL-FAMILY",
                everyCylindricalFamilyHasIndependentFiveFixtureMatrix,
                ref pass, ref fail,
                "Each of the nine cylindrical families registers a separate lifecycle case and a separate five-fixture case; every fixture owns and closes its own Inventor PartDocument, and the lifecycle includes rejected-edit Abort, snapshot rollback and Undo.");

            Check("RUNTIME-AUDIT-COVERS-CAPNECK-DIAMETER-MATRIX",
                runtimeAuditSourceForRunout.Contains("CAPNECK-DIAMETER-BOUNDARY-MATRIX") &&
                runtimeAuditSourceForRunout.Contains("RunCapNeckDiameterBoundaryMatrixCase(root, runId, result)") &&
                runtimeAuditSourceForRunout.Contains("DiameterMm = 15.0") &&
                runtimeAuditSourceForRunout.Contains("DiameterMm = 100.0") &&
                runtimeAuditSourceForRunout.Contains("PackagingThreadGeometryBuilder.Build(_app, edge, resolved, 24.0"),
                ref pass, ref fail, "Cap/Neck has five physically built independent diameter/pitch scenarios; its companion case covers repeat edit and exact Undo.");

            Check("RUNTIME-AUDIT-COVERS-NPT-FREE-DIAMETER-MATRIX",
                runtimeAuditSourceForRunout.Contains("TAPERED-NPT-FREE-DIAMETER-MATRIX") &&
                runtimeAuditSourceForRunout.Contains("RunNptFreeDiameterMatrixCase(root, runId, result)") &&
                runtimeAuditSourceForRunout.Contains("StockDiameterMm = 18.0") &&
                runtimeAuditSourceForRunout.Contains("StockDiameterMm = 30.0") &&
                runtimeAuditSourceForRunout.Contains("TryFindNptParameters(target, out ThreadParams parameters"),
                ref pass, ref fail, "NPT free geometry uses five real stock diameters with the exact installed Thread.xls designation and pitch; strict table NPT remains a separate regression.");

            Check("RUNTIME-AUDIT-COVERS-GOST-PIPE-FREE-DIAMETER-MATRIX",
                runtimeAuditSourceForRunout.Contains("TAPERED-GOST-6211-FREE-DIAMETER-MATRIX") &&
                runtimeAuditSourceForRunout.Contains("StockDiameterMm = 18.0") &&
                runtimeAuditSourceForRunout.Contains("StockDiameterMm = 30.0") &&
                runtimeAuditSourceForRunout.Contains("RunGostPipeFreeDiameterMatrixCase(root, runId, result)"),
                ref pass, ref fail, "GOST R FreeGeometry is built on five stock diameters while the Ø22 mm case separately exercises edit and exact Undo.");

            Check("METADATA-GOST-PIPE-ACCEPTS-STRICT-AND-FREE-MODES-WITH-ENUM-FAIL-CLOSED",
                !metadataRecordSource.Contains("Parameters.TaperGeometryMode != (int)TaperGeometryModeKind.StrictStandard") &&
                metadataRecordSource.Contains("Parameters.TaperGeometryMode < 0 || Parameters.TaperGeometryMode > 1") &&
                metadataTestSource.Contains("METADATA-ACCEPT-GOST-PIPE-R-FREE-GEOMETRY-D22"),
                ref pass, ref fail, "GOST metadata accepts StrictStandard and FreeGeometry while the persisted enum guard remains closed to unknown values.");

            Check("RUNTIME-AUDIT-COVERS-GOST-TR-AND-GOST-S-INTERNAL-BUILD-EDIT-UNDO",
                runtimeAuditSourceForRunout.Contains("STANDARD-INTERNAL-GOST-TRAPEZOIDAL-BUILD-EDIT-UNDO") &&
                runtimeAuditSourceForRunout.Contains("STANDARD-INTERNAL-GOST-BUTTRESS-BUILD-EDIT-UNDO") &&
                runtimeAuditSourceForRunout.Contains("RunGostInternalStandardEditUndoCase") &&
                runtimeAuditSourceForRunout.Contains("replacement.Starts = 3;") &&
                runtimeAuditSourceForRunout.Contains("replacement.SizeName = \"S 20 x 4\";") &&
                runtimeAuditSourceForRunout.Contains("replacement.Mirror = true;") &&
                runtimeAuditSourceForRunout.Contains("UndoRestoredFullSnapshot"),
                ref pass, ref fail, "Both new ГОСТ families are exercised on internal cylindrical targets with a valid replacement, left-hand/profile orientation checks, stable ThreadId, B-rep fingerprint and full Undo restore.");

            Check("RUNTIME-AUDIT-COVERS-GOST-6211-R-RC-DATA-AND-CYLINDRICAL-EXCLUSION",
                runtimeAuditSourceForRunout.Contains("TAPERED-GOST-6211-DATA-POLICY") &&
                runtimeAuditSourceForRunout.Contains("rows.Count == 16") &&
                runtimeAuditSourceForRunout.Contains("!GostPipeTaperedThreadCatalog.TryGetByDesignation(\"G 1/2\"") &&
                runtimeAuditSourceForRunout.Contains("!GostPipeTaperedThreadCatalog.TryGetByDesignation(\"Rp 1/2\"") &&
                runtimeAuditSourceForRunout.Contains("!ThreadStandards.IsKnownStandard(10)") &&
                runtimeAuditSourceForRunout.Contains("if (current == ThreadStandard.GostPipeTapered) continue;"),
                ref pass, ref fail, "Only the dedicated R/Rc tapered family is audited; all 16 table rows are validated and cylindrical G/Rp are rejected and excluded from the cylindrical sweep.");

            string gostMetricTemplateEnglish = englishCatalog == null ? null : englishCatalog.GetString("Legacy_0a4f92c7d2b64a10", CultureInfo.InvariantCulture);
            string gostTrTemplateEnglish = englishCatalog == null ? null : englishCatalog.GetString("Legacy_5f4d5e7a1a4b1301", CultureInfo.InvariantCulture);
            string gostSTemplateEnglish = englishCatalog == null ? null : englishCatalog.GetString("Legacy_8a3c9e1d4f721b02", CultureInfo.InvariantCulture);
            int templateDialogLoadStart = threadDialogSourceForPersistedOverlap.IndexOf(
                "Load += (sender, args) =>", StringComparison.Ordinal);
            int templateDialogLoadApply = templateDialogLoadStart < 0 ? -1 :
                threadDialogSourceForPersistedOverlap.IndexOf("HelivantaLocalization.Apply(this);", templateDialogLoadStart, StringComparison.Ordinal);
            int templateDialogLoadRefresh = templateDialogLoadApply < 0 ? -1 :
                threadDialogSourceForPersistedOverlap.IndexOf("RefreshStandardComboLocalization();", templateDialogLoadApply, StringComparison.Ordinal);
            int templateSettingsHandlerStart = threadDialogSourceForPersistedOverlap.IndexOf(
                "if (bSettings != null) bSettings.Click", StringComparison.Ordinal);
            int templateSettingsHandlerEnd = templateSettingsHandlerStart < 0 ? -1 :
                threadDialogSourceForPersistedOverlap.IndexOf("bHelp.Click +=", templateSettingsHandlerStart, StringComparison.Ordinal);
            string templateSettingsHandler = templateSettingsHandlerStart >= 0 && templateSettingsHandlerEnd > templateSettingsHandlerStart
                ? threadDialogSourceForPersistedOverlap.Substring(templateSettingsHandlerStart,
                    templateSettingsHandlerEnd - templateSettingsHandlerStart)
                : string.Empty;
            int templateSettingsApply = templateSettingsHandler.LastIndexOf(
                "HelivantaLocalization.Apply(this);", StringComparison.Ordinal);
            int templateSettingsRefresh = templateSettingsHandler.IndexOf(
                "RefreshStandardComboLocalization();", StringComparison.Ordinal);
            Check("THREAD-TEMPLATE-COMBO-REBUILDS-ITEMS-ON-FIRST-LOAD-AND-LANGUAGE-SWITCH",
                templateDialogLoadStart >= 0 && templateDialogLoadApply > templateDialogLoadStart &&
                templateDialogLoadRefresh > templateDialogLoadApply &&
                templateSettingsHandler.Contains("settings.ShowDialog(this)") &&
                templateSettingsHandler.Contains("DialogResult.OK") &&
                templateSettingsApply >= 0 && templateSettingsRefresh > templateSettingsApply &&
                threadDialogSourceForPersistedOverlap.Contains("ThreadStandardLocalizationPolicy.PopulateComboBoxItems(") &&
                threadDialogSourceForPersistedOverlap.Contains("ThreadStandardLocalizationPolicy.GetDisplayName(") &&
                threadStandardLocalizationPolicySourceForPersistedOverlap.Contains("List<string> displayNames = GetDisplayNames(language);") &&
                threadStandardLocalizationPolicySourceForPersistedOverlap.Contains("combo.Items.Clear();") &&
                threadStandardLocalizationPolicySourceForPersistedOverlap.Contains("combo.Items.Add(displayName);") &&
                threadStandardLocalizationPolicySourceForPersistedOverlap.Contains("combo.SelectedIndex = normalizedSelectedIndex;"),
                ref pass, ref fail, "The main dialog rebuilds Template items after first-load localization and after Settings.Save returns OK; its closed field uses GetDisplayName, while PopulateComboBoxItems delegates list creation to GetDisplayNames in the shared policy.");
            Check("THREAD-TEMPLATE-ENGLISH-GOST-NAMES-USE-LATIN-GOST",
                new[] { gostMetricTemplateEnglish, gostTrTemplateEnglish, gostSTemplateEnglish }.All(value =>
                    !string.IsNullOrWhiteSpace(value) && value.StartsWith("GOST", StringComparison.Ordinal) &&
                    value.IndexOf("ГОСТ", StringComparison.OrdinalIgnoreCase) < 0),
                ref pass, ref fail, "English Template labels for all cylindrical ГОСТ families must use Latin GOST in their visible designation.");
            Check("TAPERED-SELECTORS-AND-STRICT-CHECKBOX-ARE-HIDDEN-IN-CYLINDRICAL-MODE",
                threadDialogSourceForPersistedOverlap.Contains("SetTaperThreadTypeRowVisible(cbTaperStandard, false);") &&
                threadDialogSourceForPersistedOverlap.Contains("SetTaperThreadTypeRowVisible(cbTaperDesignation, false);") &&
                threadDialogSourceForPersistedOverlap.Contains("SetTaperThreadTypeRowVisible(cbTaperStandard, tapered);") &&
                threadDialogSourceForPersistedOverlap.Contains("SetTaperThreadTypeRowVisible(cbTaperDesignation, tapered);") &&
                threadDialogSourceForPersistedOverlap.Contains("chTaperStrictStandard.Visible = tapered;") &&
                threadDialogSourceForPersistedOverlap.Contains("_threadTypeGrid.RowStyles[row].Height = 0f;"),
                ref pass, ref fail, "When tapered mode is off, hide both complete selector rows including captions, plus the strict-standard checkbox; restore their original layout when tapered mode is enabled.");

            Check("RUNTIME-AUDIT-COVERS-GOST-6211-REAL-R-RC-BUILD-EDIT-UNDO-AND-CONE-INTEGRITY",
                runtimeAuditSourceForRunout.Contains("TAPERED-GOST-6211-EXTERNAL-R-BUILD-EDIT-UNDO") &&
                runtimeAuditSourceForRunout.Contains("TAPERED-GOST-6211-INTERNAL-RC-BUILD-EDIT-UNDO") &&
                runtimeAuditSourceForRunout.Contains("RunGostPipeTaperedRuntimeCase(root, runId, true, false, result)") &&
                runtimeAuditSourceForRunout.Contains("RunGostPipeTaperedRuntimeCase(root, runId, true, true, result)") &&
                runtimeAuditSourceForRunout.Contains("RunGostPipeTaperedRuntimeCase(root, runId, false, false, result)") &&
                runtimeAuditSourceForRunout.Contains("TAPERED-GOST-6211-EXTERNAL-R-FREE-GEOMETRY-D22-BUILD-EDIT-UNDO") &&
                runtimeAuditSourceForRunout.Contains("FreeGeometrySourceDiameterMm") &&
                runtimeAuditSourceForRunout.Contains("RejectedEditRollbackVerified") &&
                runtimeAuditSourceForRunout.Contains("TaperGeometryMode == (int)original.TaperGeometryMode") &&
                runtimeAuditSourceForRunout.Contains("RequireGostPipeIntegrityClean(doc, threadId, designation, \"after-build\")") &&
                runtimeAuditSourceForRunout.Contains("RequireGostPipeIntegrityClean(doc, threadId, designation, \"after-edit\")") &&
                runtimeAuditSourceForRunout.Contains("RequireGostPipeIntegrityClean(doc, threadId, designation, \"after-undo\")") &&
                runtimeAuditSourceForRunout.Contains("VerifyBuildRollbackAfterAbort") &&
                runtimeAuditSourceForRunout.Contains("CanUndoImmediatelyAfterEdit") &&
                runtimeAuditSourceForRunout.Contains("CommittedTransactionsImmediatelyAfterEdit") &&
                runtimeAuditSourceForRunout.Contains("updateDocument: false") &&
                runtimeAuditSourceForRunout.Contains("CanUndoExactEditAfterReadOnlyVerification") &&
                runtimeAuditSourceForRunout.Contains("CommittedTransactionsAfterReadOnlyVerification") &&
                runtimeAuditSourceForRunout.Contains("if (updateDocument) document.Update2(true);") &&
                threadEditServiceSourceForPackaging.Contains("CleanupEmptyThreadFolders.inside-tapered-edit-transaction") &&
                threadEditServiceSourceForPackaging.Contains("if (replacement.Tapered)") &&
                threadEditServiceSourceForPackaging.IndexOf("CleanupEmptyThreadFolders(doc);", threadEditServiceSourceForPackaging.IndexOf("if (replacement.Tapered)"), StringComparison.Ordinal) <
                    threadEditServiceSourceForPackaging.IndexOf("transaction.End();", threadEditServiceSourceForPackaging.IndexOf("if (replacement.Tapered)"), StringComparison.Ordinal),
                ref pass, ref fail, "Runtime audit creates actual R and Rc B-rep geometry, checks cone identity after build/edit/Undo, persists the same ThreadId, and verifies both failed-edit rollback and successful Undo.");

            Check("GOST-6211-CONE-INTEGRITY-IS-BASED-ON-SAVED-SNAPSHOT",
                threadIntegrityCheckerSource.Contains("GostPipeConeIntegrityPolicy.MatchesSnapshot") &&
                threadIntegrityCheckerSource.Contains("parameters.TaperStartDiameterMm / 20.0") &&
                threadIntegrityCheckerSource.Contains("parameters.TaperEndDiameterMm / 20.0") &&
                threadIntegrityCheckerSource.Contains("parameters.TaperReferenceLengthMm / 10.0") &&
                threadIntegrityCheckerSource.Contains("ThreadIntegrity.Finding.GostPipeConeNotFound"),
                ref pass, ref fail, "For ID 9, integrity checks the actual Cone surface, axis line, 1:16 half-angle, expansion direction, saved start/end radii and axial length; missing or changed cone fails closed.");
            int integrityFocusStart = threadIntegrityDialogSource.IndexOf(
                "private void FocusSelectedFinding()", StringComparison.Ordinal);
            int integrityFocusEnd = integrityFocusStart < 0 ? -1 : threadIntegrityDialogSource.IndexOf(
                "private void BuildDisplayRows()", integrityFocusStart, StringComparison.Ordinal);
            string integrityFocusMethod = integrityFocusStart >= 0 && integrityFocusEnd > integrityFocusStart
                ? threadIntegrityDialogSource.Substring(integrityFocusStart, integrityFocusEnd - integrityFocusStart)
                : string.Empty;
            bool integrityFocusComFailuresAreDiagnosed =
                integrityFocusMethod.Contains("if (!scanComplete)") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.NativeThreadEnumerationIncomplete") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.BrowserPaneEnumeration") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.BrowserNodeLabel.Depth=") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.BrowserNodeParent.Depth=") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.SelectFolderNode") &&
                integrityFocusMethod.Contains("ThreadIntegrityDialog.FocusFinding.ActiveViewUpdate") &&
                !integrityFocusMethod.Contains("catch { }");
            Check("THREAD-INTEGRITY-FOCUS-DIAGNOSES-COM-FALLBACKS-AND-REJECTS-PARTIAL-NATIVE-SCAN",
                integrityFocusComFailuresAreDiagnosed, ref pass, ref fail,
                "Integrity navigation may fall back when selection/visibility/zoom fails, but each suppressed COM failure is diagnosed; native ThreadFeature lookup is not resolved from an incomplete enumeration.");
            string nativeThreadOverlapGuardSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "NativeThreadOverlapGuard.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeThreadOverlapGuard.cs"), Encoding.UTF8)
                : string.Empty;
            int helivantaUnknownIntervalStart = threadIntegrityCheckerSource.IndexOf(
                "if (!TryGetThreadInterval(a, a.Target.Axis", StringComparison.Ordinal);
            int helivantaUnknownIntervalEnd = helivantaUnknownIntervalStart < 0 ? -1 :
                threadIntegrityCheckerSource.IndexOf("if (ThreadIntegrityPolicy.IntervalsOverlap", helivantaUnknownIntervalStart, StringComparison.Ordinal);
            string helivantaUnknownIntervalBlock = helivantaUnknownIntervalStart >= 0 &&
                helivantaUnknownIntervalEnd > helivantaUnknownIntervalStart
                ? threadIntegrityCheckerSource.Substring(helivantaUnknownIntervalStart,
                    helivantaUnknownIntervalEnd - helivantaUnknownIntervalStart)
                : string.Empty;
            int nativeUnknownIntervalStart = threadIntegrityCheckerSource.IndexOf(
                "if (!native.IntervalKnown)", StringComparison.Ordinal);
            int nativeUnknownIntervalEnd = nativeUnknownIntervalStart < 0 ? -1 :
                threadIntegrityCheckerSource.IndexOf("else if (!native.StandardMatched)", nativeUnknownIntervalStart, StringComparison.Ordinal);
            string nativeUnknownIntervalBlock = nativeUnknownIntervalStart >= 0 &&
                nativeUnknownIntervalEnd > nativeUnknownIntervalStart
                ? threadIntegrityCheckerSource.Substring(nativeUnknownIntervalStart,
                    nativeUnknownIntervalEnd - nativeUnknownIntervalStart)
                : string.Empty;
            int nullNativeFeatureStart = nativeThreadOverlapGuardSource.IndexOf(
                "if (feature == null)", StringComparison.Ordinal);
            int nullNativeFeatureEnd = nullNativeFeatureStart < 0 ? -1 :
                nativeThreadOverlapGuardSource.IndexOf("var snapshot =", nullNativeFeatureStart, StringComparison.Ordinal);
            string nullNativeFeatureBlock = nullNativeFeatureStart >= 0 && nullNativeFeatureEnd > nullNativeFeatureStart
                ? nativeThreadOverlapGuardSource.Substring(nullNativeFeatureStart,
                    nullNativeFeatureEnd - nullNativeFeatureStart)
                : string.Empty;
            Check("THREAD-INTEGRITY-UNKNOWN-INTERVALS-KEEP-REPORT-INCOMPLETE",
                helivantaUnknownIntervalBlock.Contains("report.ScanComplete = false;") &&
                nativeUnknownIntervalBlock.Contains("report.ScanComplete = false;") &&
                threadIntegrityCheckerSource.Contains("if (!nativeScanComplete)") &&
                threadIntegrityCheckerSource.Contains("report.ScanComplete = false;") &&
                threadIntegrityCheckerSource.Contains("ThreadIntegrityChecker.NativeEnumeration"),
                ref pass, ref fail,
                "Unknown Helivanta-group and native ThreadFeature intervals, as well as incomplete native enumeration, must never leave the integrity report marked complete.");
            Check("NATIVE-THREAD-NULL-COLLECTION-ENTRY-MARKS-ENUMERATION-INCOMPLETE",
                nullNativeFeatureBlock.Contains("complete = false;") &&
                nullNativeFeatureBlock.Contains("NativeThreadOverlapGuard.Enumerate.NullFeature") &&
                nullNativeFeatureBlock.Contains("NativeThread.Error.EnumerationFailed"),
                ref pass, ref fail,
                "A null ThreadFeatures collection entry is an incomplete inventory, even while later entries continue to be collected for diagnostics.");

            Check("RUNOUT-LOFT-AND-SEGMENTED-HELPERS-ARE-HIDDEN",
                loftVisibilityBlock.Contains("helperPlane.Visible = false;") &&
                loftVisibilityBlock.Contains("if (helperPlane.Visible)") &&
                loftVisibilityBlock.Contains("helperSketch.Visible = false;") &&
                loftVisibilityBlock.Contains("if (helperSketch.Visible)") &&
                runoutFolderNestingSource.Contains("wp.Visible = false;") &&
                runoutFolderNestingSource.Contains("if (wp.Visible)") &&
                runoutFolderNestingSource.Contains("sk.Visible = false;") &&
                runoutFolderNestingSource.Contains("if (sk.Visible)"),
                ref pass, ref fail, "Both Loft section WorkPlanes/PlanarSketches and segmented run-out helpers are explicitly hidden and checked by readback.");

            int nestedFolderCleanupStart = threadEditServiceSourceForPackaging.IndexOf(
                "private static void CleanupEmptyManagedNestedFolders(BrowserNode parentNode, int depth)", StringComparison.Ordinal);
            int nestedFolderCleanupEnd = nestedFolderCleanupStart < 0 ? -1 : threadEditServiceSourceForPackaging.IndexOf(
                "private static bool TryDeleteManagedEmptyFolder(BrowserFolder folder, string diagnosticContext)", nestedFolderCleanupStart, StringComparison.Ordinal);
            string nestedFolderCleanupMethod = nestedFolderCleanupStart >= 0 && nestedFolderCleanupEnd > nestedFolderCleanupStart
                ? threadEditServiceSourceForPackaging.Substring(nestedFolderCleanupStart, nestedFolderCleanupEnd - nestedFolderCleanupStart)
                : string.Empty;
            int managedFolderDeleteStart = nestedFolderCleanupEnd;
            int managedFolderDeleteEnd = managedFolderDeleteStart < 0 ? -1 : threadEditServiceSourceForPackaging.IndexOf(
                "private static void CleanupEmptyThreadFolders(PartDocument doc)", managedFolderDeleteStart, StringComparison.Ordinal);
            string managedFolderDeleteMethod = managedFolderDeleteStart >= 0 && managedFolderDeleteEnd > managedFolderDeleteStart
                ? threadEditServiceSourceForPackaging.Substring(managedFolderDeleteStart, managedFolderDeleteEnd - managedFolderDeleteStart)
                : string.Empty;
            int descendantVisit = nestedFolderCleanupMethod.IndexOf("CleanupEmptyManagedNestedFolders(nestedNode, depth + 1);", StringComparison.Ordinal);
            int descendantDelete = nestedFolderCleanupMethod.IndexOf("TryDeleteManagedEmptyFolder(nested,", StringComparison.Ordinal);
            Check("NESTED-MANAGED-RUNOUT-FOLDERS-CLEAN-UP-RECURSIVELY",
                nestedFolderCleanupMethod.Contains("ThreadBrowserFolderPolicy.IsManagedThreadFolder(name)") &&
                nestedFolderCleanupMethod.Contains("if (!ThreadBrowserFolderPolicy.IsManagedThreadFolder(name)) continue;") &&
                descendantVisit >= 0 && descendantDelete > descendantVisit &&
                nestedFolderCleanupMethod.Contains("MaximumManagedFolderCleanupDepth") &&
                nestedFolderCleanupMethod.Contains("CleanupEmptyManagedNestedFolders.DepthLimit") &&
                managedFolderDeleteMethod.Contains("ThreadBrowserFolderPolicy.IsManagedThreadFolder(name)") &&
                managedFolderDeleteMethod.Contains("TryGetBrowserFolderNodeEmptiness(node, context + \".EmptinessCheck\", out isEmpty)") &&
                managedFolderDeleteMethod.Contains("if (!ThreadBrowserFolderPolicy.IsManagedThreadFolder(name)) return false;") &&
                managedFolderDeleteMethod.Contains("((dynamic)folder).Delete();") &&
                managedFolderDeleteMethod.Contains("return false;") &&
                threadEditServiceSourceForPackaging.Contains("TryDeleteManagedEmptyFolder(nested,") &&
                threadEditServiceSourceForPackaging.Contains("TryDeleteManagedEmptyFolder(folder, "),
                ref pass, ref fail, "Managed descendants are traversed bottom-up; the shared delete gate re-reads managed ownership, BrowserNode, and both child collection counts immediately before Delete. Foreign, unreadable, and over-depth folders are preserved and diagnosed.");

            Check("RUNTIME-AUDIT-VERIFIES-SINGLE-THREAD-FOLDER-AND-HIDDEN-RUNOUT-HELPERS",
                runtimeAuditSourceForRunout.Contains("requireCommonThreadFolder = true") &&
                runtimeAuditSourceForRunout.Contains("SeparateRunoutFolderCount") &&
                runtimeAuditSourceForRunout.Contains("RunoutFeatureCountInThreadFolder") &&
                runtimeAuditSourceForRunout.Contains("VisibleRunoutHelperObjectCount") &&
                runtimeAuditSourceForRunout.Contains("Require(separateRunoutFolders == 0") &&
                runtimeAuditSourceForRunout.Contains("Require(runoutFeatureCount > 0") &&
                runtimeAuditSourceForRunout.Contains("Require(visibleRunoutHelpers == 0") &&
                runtimeAuditSourceForRunout.Contains("Require(expectedRootFolder != null") &&
                !runtimeAuditSourceForRunout.Contains("RunoutFolderCollapsed"),
                ref pass, ref fail, "Inventor runtime audit verifies a present common thread folder, no separate run-out folder, run-out PartFeatures inside it and hidden run-out WorkPlanes/PlanarSketches.");

            // Undo/Redo must be tied to the actual transaction kind and exact edited ThreadId.
            const string buildTxPrefix = "Helivanta: построение резьбы";
            const string editTxPrefix = "Helivanta: редактирование резьбы";
            string parsedThreadId;
            Check("UNDO-CLASSIFY-BUILD-TRANSACTION",
                ThreadEditSafetyPolicy.ClassifyTransaction(buildTxPrefix, buildTxPrefix, editTxPrefix, out parsedThreadId) == HelivantaTransactionKind.Build &&
                parsedThreadId == null,
                ref pass, ref fail, "Транзакция обычного построения классифицируется отдельно от редактирования.");
            Check("UNDO-CLASSIFY-EDIT-TRANSACTION-ID",
                ThreadEditSafetyPolicy.ClassifyTransaction(editTxPrefix + " thread-A", buildTxPrefix, editTxPrefix, out parsedThreadId) == HelivantaTransactionKind.Edit &&
                parsedThreadId == "thread-A",
                ref pass, ref fail, "Транзакция редактирования содержит ID изменённой группы.");
            Check("UNDO-REJECT-AMBIGUOUS-EDIT-TRANSACTION",
                ThreadEditSafetyPolicy.ClassifyTransaction(editTxPrefix, buildTxPrefix, editTxPrefix, out parsedThreadId) == HelivantaTransactionKind.Other &&
                parsedThreadId == null,
                ref pass, ref fail, "Транзакция редактирования без ThreadId игнорируется как неоднозначная.");
            Check("UNDO-MATCHES-CURRENT-THREAD",
                ThreadEditSafetyPolicy.IsEditTransactionForThread(editTxPrefix + " thread-A", buildTxPrefix, editTxPrefix, "thread-A"),
                ref pass, ref fail, "Событие Undo/Redo относится к открытому ThreadId только при точном совпадении.");
            Check("UNDO-IGNORES-OTHER-THREAD",
                !ThreadEditSafetyPolicy.IsEditTransactionForThread(editTxPrefix + " thread-B", buildTxPrefix, editTxPrefix, "thread-A"),
                ref pass, ref fail, "Undo/Redo другой резьбы не выдаётся за изменение текущей группы.");
            Check("EDIT-NOMINAL-DIAMETER-PRESERVED-ON-STANDARD-SWITCH",
                ThreadEditSafetyPolicy.IsNominalDiameterPreserved(45.0, 45.0),
                ref pass, ref fail, "При смене M45 на Tr45 номинальный диаметр остаётся Ø45 мм.");
            Check("EDIT-NOMINAL-DIAMETER-REJECTS-M200",
                !ThreadEditSafetyPolicy.IsNominalDiameterPreserved(45.0, 200.0),
                ref pass, ref fail, "Редактирование существующей Ø45 резьбы не может заменить её на Ø200 и изменить базовый цилиндр.");
            Check("EDIT-NOMINAL-DIAMETER-REJECTS-INVALID",
                !ThreadEditSafetyPolicy.IsNominalDiameterPreserved(45.0, double.NaN) &&
                !ThreadEditSafetyPolicy.IsNominalDiameterPreserved(45.0, 0.0),
                ref pass, ref fail, "Некорректный или неположительный диаметр отклоняется до открытия транзакции.");

            Check("STANDARD-DEFAULT-GOST-KEY-IS-LANGUAGE-NEUTRAL",
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("GOST", 7) == (int)ThreadStandard.GostMetric &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("ISO", 7) == (int)ThreadStandard.ISO &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("ANSI/UN", 7) == (int)ThreadStandard.ANSI &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("BSW", 7) == (int)ThreadStandard.BSW &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("Tr", 7) == (int)ThreadStandard.Tr &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("S", 7) == (int)ThreadStandard.S &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("Rd", 7) == (int)ThreadStandard.Rd &&
                ThreadStandardSelectionPolicy.ResolveDefaultIndex("unknown", 7) == (int)ThreadStandard.ISO,
                ref pass, ref fail, "Параметр стандарта по умолчанию хранится как стабильный ключ и не зависит от языка интерфейса.");
            Check("STANDARD-IGNORE-TRANSIENT-INDEX-CHANGED",
                !ThreadStandardSelectionPolicy.ShouldApplyChange(false, false, 4, 7),
                ref pass, ref fail, "Промежуточный SelectedIndexChanged не запускает перестроение каталога до фиксации выбора.");
            Check("STANDARD-APPLY-COMMITTED-S",
                ThreadStandardSelectionPolicy.ShouldApplyChange(true, false, (int)ThreadStandard.S, 7),
                ref pass, ref fail, "Подтверждённый выбор стандарта S применяется после закрытия выпадающего списка.");
            Check("STANDARD-IGNORE-PROGRAMMATIC-CHANGE",
                !ThreadStandardSelectionPolicy.ShouldApplyChange(true, true, (int)ThreadStandard.S, 7),
                ref pass, ref fail, "Программная загрузка параметров не запускает повторное пользовательское переключение.");
            Check("STANDARD-RESTORE-LAST-COMMITTED-AFTER-EMPTY",
                ThreadStandardSelectionPolicy.ResolveStableIndex(-1, (int)ThreadStandard.S, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Пустое промежуточное значение ComboBox восстанавливается к последнему подтверждённому стандарту S.");
            Check("STANDARD-COMMITTED-S-SURVIVES-EMPTY-CLOSED-FIELD",
                ThreadStandardSelectionPolicy.ResolveAfterDropdownClose(true, (int)ThreadStandard.S, -1, (int)ThreadStandard.Tr, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Подтверждённый стандарт S сохраняется, даже если нативное поле после закрытия временно сообщает SelectedIndex=-1.");
            Check("STANDARD-STABLE-CLOSED-SELECTION-WINS",
                ThreadStandardSelectionPolicy.ResolveAfterDropdownClose(false, -1, (int)ThreadStandard.S, (int)ThreadStandard.Tr, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Устойчивый выбор S применяется и при запоздавшем уведомлении фиксации.");
            Check("STANDARD-CANCEL-RESTORES-LAST-COMMITTED",
                ThreadStandardSelectionPolicy.ResolveAfterDropdownClose(false, (int)ThreadStandard.S, -1, (int)ThreadStandard.Tr, 7) == (int)ThreadStandard.Tr,
                ref pass, ref fail, "Неподтверждённый промежуточный S не применяется после отмены раскрытого списка.");
            Check("STANDARD-COMMIT-USES-VALID-CURRENT-INDEX",
                ThreadStandardSelectionPolicy.ResolveCommittedIndex((int)ThreadStandard.S, (int)ThreadStandard.Tr, (int)ThreadStandard.ISO, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Подтверждённый индекс S сразу становится источником состояния стандарта.");
            Check("STANDARD-COMMIT-USES-CAPTURED-INDEX-AFTER-NATIVE-CLEAR",
                ThreadStandardSelectionPolicy.ResolveCommittedIndex(-1, (int)ThreadStandard.S, (int)ThreadStandard.ISO, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Если поле ComboBox уже пусто, подтверждённый кандидат S сохраняется для построения.");
            Check("STANDARD-COMMIT-PREFERS-NEW-CANDIDATE-OVER-STALE-CURRENT",
                ThreadStandardSelectionPolicy.ResolveCommittedIndex((int)ThreadStandard.Tr, (int)ThreadStandard.S, (int)ThreadStandard.Tr, 7) == (int)ThreadStandard.S,
                ref pass, ref fail, "Позднее уведомление со старым индексом Tr не отменяет уже выбранный S.");
            Check("STANDARD-COMMIT-FALLS-BACK-ONLY-WITHOUT-CANDIDATE",
                ThreadStandardSelectionPolicy.ResolveCommittedIndex(-1, -1, (int)ThreadStandard.Tr, 7) == (int)ThreadStandard.Tr,
                ref pass, ref fail, "Предыдущий стандарт сохраняется только если нет нового действительного выбора.");
            Check("STANDARD-COMMIT-NEW-GOST-INDEX",
                ThreadStandardSelectionPolicy.ResolveCommittedIndex((int)ThreadStandard.GostMetric, (int)ThreadStandard.ISO, (int)ThreadStandard.ISO, 7) == (int)ThreadStandard.GostMetric,
                ref pass, ref fail, "Новый индекс ГОСТ 6 принимается ComboBox без изменения старых индексов.");
            Check("STANDARD-CLOSED-FIELD-USES-COMMITTED-LABEL",
                ThreadStandardSelectionPolicy.ResolveFieldDisplayText("S", "", false) == "S" &&
                ThreadStandardSelectionPolicy.ResolveFieldDisplayText("S", "Tr", false) == "S" &&
                ThreadStandardSelectionPolicy.ResolveFieldDisplayText("S", "S", true) == "S",
                ref pass, ref fail, "Закрытое поле показывает подтверждённый стандарт даже при пустом/устаревшем native Text.");

            Check("TECHNOLOGY-STABLE-INDEX-RESOLUTION",
                ThreadTechnologySelectionPolicy.ResolveStableIndex(-1, 3, 5) == 3 &&
                ThreadTechnologySelectionPolicy.ResolveStableIndex(2, 3, 5) == 2 &&
                ThreadTechnologySelectionPolicy.ResolveStableIndex(-1, 8, 5) == 0,
                ref pass, ref fail, "Технология корректно использует стабильный индекс при пустом или недопустимом native SelectedIndex.");

            Check("TECHNOLOGY-COMMIT-PREFERS-NEW-FDM-CANDIDATE",
                ThreadTechnologySelectionPolicy.ResolveCommittedIndex(0, 3, 0, 5) == 3,
                ref pass, ref fail, "При первом выборе FDM запоздавший индекс Металл не отменяет выбор пользователя.");
            Check("TECHNOLOGY-COMMIT-SURVIVES-NATIVE-CLEAR",
                ThreadTechnologySelectionPolicy.ResolveCommittedIndex(-1, 3, 0, 5) == 3,
                ref pass, ref fail, "Подтверждённый FDM сохраняется, даже если ComboBox очищает SelectedIndex при закрытии списка.");
            Check("TECHNOLOGY-CLOSED-FIELD-RETAINS-COMMITTED-FDM",
                ThreadTechnologySelectionPolicy.ResolveAfterDropdownClose(true, 3, -1, 0, 5) == 3,
                ref pass, ref fail, "После закрытия списка остаётся подтверждённая технология FDM.");
            Check("TECHNOLOGY-CANCEL-RESTORES-PREVIOUS-CHOICE",
                ThreadTechnologySelectionPolicy.ResolveAfterDropdownClose(false, 3, 0, 0, 5) == 0,
                ref pass, ref fail, "Без подтверждения выбора отмена списка возвращает прежнюю технологию.");
            Check("TECHNOLOGY-REOPEN-IGNORES-STALE-NATIVE-INDEX",
                ThreadTechnologySelectionPolicy.ResolveAfterDropdownClose(false, -1, 0, 3, 5) == 3,
                ref pass, ref fail, "При повторном запуске формы устаревший native индекс Metal не отменяет подтверждённый FDM.");
            Check("TECHNOLOGY-CLOSED-FIELD-USES-COMMITTED-LABEL",
                ThreadTechnologySelectionPolicy.ResolveFieldDisplayText("FDM-печать (0,3)", "Металл (0)", false) == "FDM-печать (0,3)" &&
                ThreadTechnologySelectionPolicy.ResolveFieldDisplayText("FDM-печать (0,3)", "Металл (0)", true) == "Металл (0)",
                ref pass, ref fail, "Закрытое поле показывает сохранённую технологию, но во время раскрытия не заменяет native текст.");
            decimal fdmClearance, slsClearance, ignoredClearance;
            Check("TECHNOLOGY-PRESET-CLEARANCE-MAPPING",
                ThreadTechnologySelectionPolicy.TryGetPresetClearance(3, out fdmClearance) && fdmClearance == 0.3m &&
                ThreadTechnologySelectionPolicy.TryGetPresetClearance(2, out slsClearance) && slsClearance == 0.15m &&
                ThreadTechnologySelectionPolicy.FindPresetIndex(0.30, 0.0006) == 3 &&
                ThreadTechnologySelectionPolicy.FindPresetIndex(0.22, 0.0006) == 4 &&
                ThreadTechnologySelectionPolicy.IsPresetIndex(3) && !ThreadTechnologySelectionPolicy.IsPresetIndex(4) &&
                !ThreadTechnologySelectionPolicy.TryGetPresetClearance(4, out ignoredClearance),
                ref pass, ref fail, "FDM задаёт 0,30 мм, SLS — 0,15 мм, а «Другое» сохраняет ручной зазор.");

            Check("CHAMFER-COMMIT-PREFERS-BOTH-ENDS-CANDIDATE",
                ThreadChamferSelectionPolicy.ResolveCommittedIndex(0, 2, 0, 3) == 2,
                ref pass, ref fail, "Подтверждённый выбор «С обоих краёв» имеет приоритет над старым индексом «В начале».");
            Check("CHAMFER-COMMIT-SURVIVES-NATIVE-CLEAR",
                ThreadChamferSelectionPolicy.ResolveCommittedIndex(-1, 2, 0, 3) == 2,
                ref pass, ref fail, "Выбор «С обоих краёв» сохраняется, даже если нативный ComboBox очищает SelectedIndex.");
            Check("CHAMFER-CLOSED-FIELD-PRESERVES-BOTH-ENDS",
                ThreadChamferSelectionPolicy.ResolveAfterDropdownClose(true, 2, -1, 0, 3) == 2,
                ref pass, ref fail, "После закрытия списка остаётся подтверждённое расположение фасок с обоих краёв.");
            Check("CHAMFER-LATE-OLD-INDEX-DOES-NOT-ROLL-BACK",
                ThreadChamferSelectionPolicy.ResolveAfterDropdownClose(true, 2, 0, 2, 3) == 2,
                ref pass, ref fail, "Запоздалое уведомление со старым индексом не отменяет подтверждённый выбор обоих краёв.");
            Check("CHAMFER-CANCEL-RESTORES-LAST-COMMITTED",
                ThreadChamferSelectionPolicy.ResolveAfterDropdownClose(false, 2, -1, 0, 3) == 0,
                ref pass, ref fail, "Закрытие без подтверждения возвращает предыдущее расположение фаски.");
            Check("CHAMFER-STABLE-CLOSED-SELECTION-WINS",
                ThreadChamferSelectionPolicy.ResolveAfterDropdownClose(false, -1, 2, 0, 3) == 2,
                ref pass, ref fail, "Устойчивый новый выбор расположения принимается даже при запоздавшем уведомлении фиксации.");
            Check("CHAMFER-CLOSED-FIELD-USES-COMMITTED-LABEL",
                ThreadChamferSelectionPolicy.ResolveFieldDisplayText("С обоих краёв", "В начале", false) == "С обоих краёв" &&
                ThreadChamferSelectionPolicy.ResolveFieldDisplayText("С обоих краёв", "В начале", true) == "В начале",
                ref pass, ref fail, "Закрытое поле показывает подтверждённый вариант, а открытый список сохраняет нативное управление.");
            Check("CHAMFER-MODE-USES-COMMITTED-INDEX",
                ThreadChamferSelectionPolicy.ResolveChamferMode(true, 0, 2, 3) == 3 &&
                ThreadChamferSelectionPolicy.ResolveChamferMode(false, 2, 2, 3) == 0,
                ref pass, ref fail, "При построении подтверждённый индекс обоих краёв сильнее устаревшего индекса начала; выключено = 0.");

            Check("EDIT-ROLLBACK-VERIFIED-ONLY-WHEN-COMPLETE",
                ThreadEditSafetyPolicy.IsRollbackVerified(true, true, true, 12, 12),
                ref pass, ref fail, "Откат подтверждается только при успешном Abort, совпадении записи и полном составе группы.");
            Check("EDIT-ROLLBACK-REJECTS-ABORT-FAILURE",
                !ThreadEditSafetyPolicy.IsRollbackVerified(false, true, true, 12, 12),
                ref pass, ref fail, "Ошибка Abort не позволяет объявить восстановление подтверждённым.");
            Check("EDIT-ROLLBACK-REJECTS-RECORD-MISMATCH",
                !ThreadEditSafetyPolicy.IsRollbackVerified(true, true, false, 12, 12),
                ref pass, ref fail, "Откат отклоняется как неподтверждённый, если параметры/снимок исходной группы изменились.");
            Check("EDIT-ROLLBACK-REJECTS-INCOMPLETE-GROUP",
                !ThreadEditSafetyPolicy.IsRollbackVerified(true, true, true, 12, 11) &&
                !ThreadEditSafetyPolicy.IsRollbackVerified(true, false, false, 12, 0),
                ref pass, ref fail, "Неполная или отсутствующая исходная группа не считается восстановленной.");

            int[] deleteOrder;
            bool hasDeleteOrder = ThreadEditSafetyPolicy.TryBuildDeletionOrder(
                new[] { ThreadEditMemberKind.WorkFeature, ThreadEditMemberKind.Sketch, ThreadEditMemberKind.Feature,
                        ThreadEditMemberKind.WorkFeature, ThreadEditMemberKind.Sketch, ThreadEditMemberKind.Feature },
                out deleteOrder);
            Check("EDIT-DELETE-PLAN-FEATURES-FIRST-REVERSE", hasDeleteOrder &&
                deleteOrder.SequenceEqual(new[] { 5, 2, 4, 1, 3, 0 }),
                ref pass, ref fail, "План удаления: фичи в обратном порядке создания, затем эскизы, затем рабочие объекты.");
            Check("EDIT-DELETE-PLAN-REJECT-UNSUPPORTED", !ThreadEditSafetyPolicy.TryBuildDeletionOrder(
                new[] { ThreadEditMemberKind.Feature, ThreadEditMemberKind.Unsupported }, out _),
                ref pass, ref fail, "Неизвестный тип в группе отклоняется до первого удаления.");


            // Build155: pure preflight contract for the isolated packaging-thread prototype.
            Check("PACKAGING-ONE-START-LEAD-EQUALS-PITCH",
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.0, 2.0, false, out var pack1, out _) &&
                Math.Abs(pack1.LeadMm - 2.7) < 1e-12,
                ref pass, ref fail, "Для однозаходной резьбы lead равен pitch.");
            Check("PACKAGING-TWO-START-LEAD-TWICE-PITCH",
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 2, 0.025, 0.0, 2.0, false, out var pack2, out _) &&
                Math.Abs(pack2.LeadMm - 5.4) < 1e-12,
                ref pass, ref fail, "Для двух заходов осевое перемещение за оборот равно двум шагам.");
            Check("PACKAGING-THREE-START-LEAD-THREE-PITCH",
                PackagingThreadGeometryPolicy.TryResolve(28, 1.5, 3, 0.025, 0.0, 2.0, false, out var pack3, out _) &&
                Math.Abs(pack3.LeadMm - 4.5) < 1e-12,
                ref pass, ref fail, "Для трёх заходов lead равен трём pitch.");
            Check("PACKAGING-DEFAULT-HEIGHT-RATIO-IS-0.05",
                Math.Abs(PackagingThreadGeometryPolicy.DefaultHeightRatio - 0.05) < 1e-12 &&
                PackagingThreadGeometryPolicy.TryResolve(22.0, 2.7, 1,
                    PackagingThreadGeometryPolicy.DefaultHeightRatio, 0.2, 2.0, false, out var packDefaultH, out _) &&
                Math.Abs(packDefaultH.HeightMm - 1.1) < 1e-12,
                ref pass, ref fail, "Значение по умолчанию H/D=0,05 даёт H=1,1 мм при D=22 мм и не ограничивается шагом 2,7 мм.");
            Check("PACKAGING-PCO1881-DETAIL-B-BELL-CONTOUR-ROOT-TANGENCY",
                PackagingThreadGeometryPolicy.RootFilletRadiusMm == 0.5 &&
                PackagingThreadGeometryPolicy.CrestFilletRadiusMm == 0.4 &&
                Math.Abs(PackagingThreadGeometryPolicy.PcoLoadFlankAngleDegrees - 20.0) < 1e-12 &&
                Math.Abs(PackagingThreadGeometryPolicy.PcoRampFlankAngleDegrees - 10.0) < 1e-12 &&
                PackagingThreadGeometryPolicy.TryResolvePcoBellProfileDimensionsMm(1.08, 0.22, 1.1,
                    out var pcoBell, out _) &&
                Math.Abs(pcoBell.RadiusScale - 1.0) < 1e-12 &&
                Math.Abs(pcoBell.EffectiveRootFilletRadiusMm - 0.5) < 1e-12 &&
                Math.Abs(pcoBell.EffectiveCrestFilletRadiusMm - 0.4) < 1e-12 &&
                pcoBell.CrownLandWidthMm >= PackagingThreadGeometryPolicy.MinPcoProfileStraightMm &&
                Math.Abs(Math.Atan2(pcoBell.LeftCrestFlankHeightMm - pcoBell.LeftRootFlankHeightMm,
                    pcoBell.LeftCrestFlankXmm - pcoBell.LeftRootFlankXmm) * 180.0 / Math.PI - 70.0) < 1e-8 &&
                Math.Abs(Math.Atan2(pcoBell.RightCrestFlankHeightMm - pcoBell.RightRootFlankHeightMm,
                    pcoBell.RightRootFlankXmm - pcoBell.RightCrestFlankXmm) * 180.0 / Math.PI - 80.0) < 1e-8 &&
                Math.Abs(pcoBell.LeftRootFlankHeightMm - 0.5 * (1.0 - Math.Cos(70.0 * Math.PI / 180.0))) < 1e-9,
                ref pass, ref fail, "Detail B: колоколообразный контур с корневыми дугами, касательными к поверхности основы, плечами 20°/10° и вершиной R0,40.");
            Check("PACKAGING-EXTERNAL-RELIEF-POST-THREAD-EXTENSION-IS-0.75MM",
                PackagingThreadGeometryPolicy.ExternalReliefPostThreadExtensionMm >= 0.5 &&
                PackagingThreadGeometryPolicy.ExternalReliefPostThreadExtensionMm <= 1.0 &&
                Math.Abs(PackagingThreadGeometryPolicy.ExternalReliefPostThreadExtensionMm - 0.75) < 1e-12,
                ref pass, ref fail, "Наружная проточка имеет отдельный прямой участок 0,75 мм за концом резьбы; прорези остаются ограничены её собственной длиной резьбы.");
            Check("PACKAGING-HEIGHT-SCALES-WITH-DIAMETER",
                PackagingThreadGeometryPolicy.TryResolve(20, 2.7, 1, 0.02, 0.0, 10.0, false, out var packSmall, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(40, 2.7, 1, 0.02, 0.0, 10.0, false, out var packLarge, out _) &&
                Math.Abs(packLarge.HeightMm / packSmall.HeightMm - 2.0) < 1e-12,
                ref pass, ref fail, "Высота пропорциональна диаметру внутри абсолютных безопасных пределов.");
            Check("PACKAGING-HEIGHT-LOWER-BOUND-IS-EXPLICIT",
                !PackagingThreadGeometryPolicy.TryResolve(10, 0.5, 1, 0.005, 0.0, 1.0, false, out _, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(30, 1.0, 1, 0.005, 0.0, 1.0, false, out var packMin, out _) &&
                Math.Abs(packMin.HeightMm - 0.15) < 1e-12,
                ref pass, ref fail, "Минимальная высота 0,15 мм проверяется явно и не достигается скрытым изменением H/D.");
            Check("PACKAGING-NO-GLOBAL-HEIGHT-OR-PITCH-CEILING",
                PackagingThreadGeometryPolicy.TryResolve(300, 22.5, 1, 0.05, 0.0, 25.0, false, out var packD300, out _) &&
                Math.Abs(packD300.HeightMm - 15.0) < 1e-9 &&
                Math.Abs(packD300.EmbedMm - 3.0) < 1e-9 &&
                Math.Abs(packD300.HeightMm + packD300.EmbedMm - 0.8 * packD300.PitchMm) < 1e-9 &&
                !PackagingThreadGeometryPolicy.TryResolve(300, 22.45, 1, 0.05, 0.0, 25.0, false, out _, out _),
                ref pass, ref fail, "Для D=300 мм и H/D=0,05 сохраняются H=15 мм и embed=3 мм; геометрическое условие требует P≥22,5 мм, без глобального потолка 8/12 мм.");
            double packD300MinimumPitch = 0.0; string packD300PitchError = null;
            bool packD300AutoPitchOk = PackagingThreadGeometryPolicy.TryFindMinimumBuildablePitchMm(
                300.0, 1, 0.05, 0.0, 25.0, false, 10.0, false, true, true,
                out packD300MinimumPitch, out packD300PitchError);
            Check("PACKAGING-D300-AUTO-PITCH-IS-GEOMETRY-BOUNDED",
                packD300AutoPitchOk && Math.Abs(packD300MinimumPitch - 22.5) < 1e-8,
                ref pass, ref fail, "Автоподбор для D=300 мм должен перейти через бывший предел 12 мм и найти P=22,50 мм; " + (packD300PitchError ?? ""));
            double noPitchCandidate = 0.0, noPitchAxial = 0.0;
            string noPitchDiagnostic = null;
            PackagingThreadGeometryPolicy.ResolvedParameters noPitchParameters;
            PackagingThreadPolicyFailure noPitchFailure;
            bool noPitchAtShortLength = !PackagingThreadGeometryPolicy.TryFindFirstAnalyticallyBuildablePitchAtOrAboveMm(
                0.25, 22.0, 1, 0.05, 0.0, 10.0, false, 0.5, false, true, true,
                out noPitchCandidate, out noPitchParameters, out noPitchAxial, out noPitchDiagnostic, out noPitchFailure);
            PackagingThreadPolicyFailure d350Pitch10Failure;
            bool d350PitchGuardRegression =
                !PackagingThreadGeometryPolicy.TryResolve(350.0, 10.0, 2, 0.02, 0.0, 11.98, false,
                    out _, out _, out d350Pitch10Failure) &&
                d350Pitch10Failure != null && d350Pitch10Failure.Code == "ProfileDepthExceedsPitch" &&
                PackagingThreadGeometryPolicy.TryResolve(350.0, 10.5, 2, 0.02, 0.0, 11.98, false,
                    out var d350Pitch105, out _, out _) &&
                Math.Abs(d350Pitch105.HeightMm - 7.0) < 1e-9 &&
                Math.Abs(d350Pitch105.EmbedMm - 1.4) < 1e-9;
            Check("PACKAGING-AUTO-PITCH-FAILURE-USES-STABLE-LOCALIZATION-KEY-AND-ARGS",
                noPitchAtShortLength && noPitchFailure != null &&
                noPitchFailure.Code == "ProfileDepthExceedsPitch" &&
                noPitchFailure.ResourceKey == "PackagingThreadPolicy.ProfileDepthExceedsPitch" &&
                noPitchFailure.FormatArgs.Length == 4 && noPitchFailure.FormatArgs[0] is double &&
                noPitchFailure.FormatArgs[1] is double && noPitchFailure.FormatArgs[2] is double &&
                noPitchFailure.FormatArgs[3] is double && !string.IsNullOrWhiteSpace(noPitchDiagnostic) &&
                d350PitchGuardRegression,
                ref pass, ref fail, "Typed pitch diagnostics remain stable; for D=350, H=7 and embed=1.4, P=10 is rejected while the exact safe boundary P=10.5 remains valid.");
            double fixedLengthPitch = 0.0, fixedLengthAxial = 0.0;
            string fixedLengthDiagnostic = null;
            PackagingThreadGeometryPolicy.ResolvedParameters fixedLengthParameters;
            PackagingThreadPolicyFailure fixedLengthFailure;
            bool fixedLengthReject = !PackagingThreadGeometryPolicy.TryFindFirstAnalyticallyBuildablePitchAtOrAboveMm(
                4.0, 22.0, 1, 0.05, 0.0, 10.0, false, 1.0, false, true, true,
                out fixedLengthPitch, out fixedLengthParameters, out fixedLengthAxial, out fixedLengthDiagnostic, out fixedLengthFailure);
            Check("PACKAGING-AUTO-PITCH-FIXED-LENGTH-FAILURE-IS-LOCALIZABLE",
                fixedLengthReject && fixedLengthFailure != null &&
                fixedLengthFailure.Code == "AutoPitchPitchExceedsLengthLimit" &&
                fixedLengthFailure.ResourceKey == "PackagingThreadPolicy.AutoPitch.FixedLengthPitchTooLarge" &&
                fixedLengthFailure.FormatArgs.Length == 2,
                ref pass, ref fail, "Ограничение осевой длины должно иметь ресурсный ключ и числовые аргументы для RU/EN.");
            Check("PACKAGING-HEIGHT-IS-NOT-SILENTLY-CLAMPED",
                !PackagingThreadGeometryPolicy.TryResolve(100, 2.0, 1, 0.06, 0.0, 10.0, false, out _, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(100, 9.0, 1, 0.06, 0.0, 10.0, false, out var packFullHeight, out _) &&
                Math.Abs(packFullHeight.HeightMm - 6.0) < 1e-9 &&
                Math.Abs(packFullHeight.HeightMm + packFullHeight.EmbedMm - 7.2) < 1e-9,
                ref pass, ref fail, "Недостаточный ручной шаг отклоняется, а при достаточном шаге профиль сохраняет H=D×(H/D), не уменьшаясь автоматически.");
            var packD22 = new PackagingThreadGeometryPolicy.ResolvedParameters();
            var packD31 = new PackagingThreadGeometryPolicy.ResolvedParameters();
            var packD52 = new PackagingThreadGeometryPolicy.ResolvedParameters();
            bool diametersResolved =
                PackagingThreadGeometryPolicy.TryResolve(22, 2.7, 3, 0.025, 0.0, 10.0, false, out packD22, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(31, 2.7, 3, 0.025, 0.0, 10.0, false, out packD31, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(52, 2.7, 3, 0.025, 0.0, 10.0, false, out packD52, out _);
            double packW22 = diametersResolved ? PackagingThreadGeometryPolicy.ResolveProfileBaseWidthMm(2.7, packD22.HeightMm, packD22.EmbedMm) : 0;
            double packW31 = diametersResolved ? PackagingThreadGeometryPolicy.ResolveProfileBaseWidthMm(2.7, packD31.HeightMm, packD31.EmbedMm) : 0;
            double packW52 = diametersResolved ? PackagingThreadGeometryPolicy.ResolveProfileBaseWidthMm(2.7, packD52.HeightMm, packD52.EmbedMm) : 0;
            Check("PACKAGING-DIAMETER-22-31-52-PROFILE-ENVELOPE",
                diametersResolved && packW22 <= 2.7 * PackagingThreadGeometryPolicy.MaxProfileBaseWidthPitchRatio + 1e-12 &&
                packW31 > packW22 && packW52 >= packW31 &&
                packW52 <= 2.7 * PackagingThreadGeometryPolicy.MaxProfileBaseWidthPitchRatio + 1e-12 &&
                (packD52.HeightMm + packD52.EmbedMm) <= 2.7 * PackagingThreadGeometryPolicy.MaxProfileDepthPitchRatio + 1e-9,
                ref pass, ref fail, "На D22/D31/D52 с P=2,7 мм сечение расширяет основания по мере роста глубины и остаётся в пределах шага.");
            Check("PACKAGING-CLEARANCE-INDEPENDENT",
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.0, 10.0, false, out var packNom, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.2, 10.0, false, out var packPrint, out _) &&
                Math.Abs(packNom.HeightMm - packPrint.HeightMm) < 1e-12 &&
                Math.Abs(packPrint.ClearanceMm - 0.2) < 1e-12,
                ref pass, ref fail, "Зазор печати сохраняется отдельно и не меняет номинальную высоту.");
            Check("PACKAGING-AUTO-DETECT-CYLINDRICAL-FACE-TYPE",
                PackagingThreadGeometryPolicy.IsInternalFromFaceParamReversed(false) &&
                !PackagingThreadGeometryPolicy.IsInternalFromFaceParamReversed(true),
                ref pass, ref fail, "Ориентация цилиндрической грани Inventor определяет внутреннюю поверхность отверстия (IsParamReversed=false) и наружную поверхность вала (true).");
            Check("PACKAGING-INTERNAL-AND-EXTERNAL-SAME-NOMINAL-PROFILE",
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.0, 10.0, true, out var packInternal, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.0, 10.0, false, out var packExternal, out _) &&
                Math.Abs(packInternal.HeightMm - packExternal.HeightMm) < 1e-12,
                ref pass, ref fail, "Направление выступа выбирается ролью внутренней/наружной геометрии, а номинальная высота не меняется.");
            Check("PACKAGING-INTERNAL-AND-EXTERNAL-POSITIVE-RIB-USE-MATCHED-SECTION",
                PackagingThreadGeometryPolicy.TryResolveProfileRadiiMm(14.0, 0.7, 0.14, true, out double innerRoot, out double innerCrest, out _) &&
                innerRoot > 14.0 && innerCrest < 14.0 && innerRoot > innerCrest &&
                PackagingThreadGeometryPolicy.TryResolveProfileRadiiMm(14.0, 0.7, 0.14, false, out double outerRoot, out double outerCrest, out _) &&
                outerRoot < 14.0 && outerCrest > 14.0 && outerRoot < outerCrest,
                ref pass, ref fail, "Обе ветки формируют положительный валик: наружный выступает над уменьшенной основой, внутренний — внутрь расширенного отверстия.");
            Check("PACKAGING-INTERNAL-PROFILE-REJECTS-INVALID-RADII",
                !PackagingThreadGeometryPolicy.TryResolveProfileRadiiMm(0.05, 0.2, 0.1, true, out _, out _, out _) &&
                !PackagingThreadGeometryPolicy.TryResolveProfileRadiiMm(14.0, 20.0, 0.1, true, out _, out _, out _),
                ref pass, ref fail, "Внутренний валик отклоняется при отрицательном радиусе или неверном взаимном расположении вершин.");
            Check("PACKAGING-RUNOUT-PLAN-BOTH-ENDS-PRESERVES-CORE",
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 8.1, 0.775, 0.155, 1.674, false, true, true, out var bothRunouts, out _) &&
                bothRunouts.StartLengthMm > 0.0 && bothRunouts.EndLengthMm > 0.0 &&
                Math.Abs(bothRunouts.TotalLengthMm - bothRunouts.StartLengthMm - bothRunouts.CoreLengthMm - bothRunouts.EndLengthMm) < 1e-9 &&
                bothRunouts.CoreLengthMm >= 8.1 * PackagingThreadRunoutPolicy.MinimumCoreLeadFraction - 1e-9 &&
                bothRunouts.StartSectionCount >= PackagingThreadRunoutPolicy.MinimumSectionCount &&
                bothRunouts.EndSectionCount <= PackagingThreadRunoutPolicy.MaximumSectionCount,
                ref pass, ref fail, "Два сбега сохраняют заданную длину на каждом конце и оставляют минимум четверти оборота основного Coil.");
            Check("PACKAGING-RUNOUT-PLAN-INDEPENDENT-END-TOGGLES",
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 8.1, 0.775, 0.155, 1.674, false, true, false, out var startOnly, out _) &&
                startOnly.StartLengthMm > 0.0 && startOnly.EndLengthMm == 0.0 && startOnly.EndSectionCount == 0 &&
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 8.1, 0.775, 0.155, 1.674, false, false, true, out var endOnly, out _) &&
                endOnly.StartLengthMm == 0.0 && endOnly.EndLengthMm > 0.0 && endOnly.StartSectionCount == 0,
                ref pass, ref fail, "Начальный и конечный сбеги включаются независимо из UI.");
            Check("PACKAGING-RUNOUT-EXTERNAL-CAPPED-AT-14-DEGREES",
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 8.1, 0.775, 0.155, 1.674, false, true, true, out var externalRunouts, out _) &&
                Math.Abs(externalRunouts.StartLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                Math.Abs(externalRunouts.EndLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                Math.Abs(externalRunouts.TargetTurnFraction * 360.0 - 14.0) < 1e-12 &&
                Math.Abs(externalRunouts.CoreLengthMm - (10.0 - 2.0 * 8.1 * 14.0 / 360.0)) < 1e-9 &&
                !externalRunouts.IsInternalThread,
                ref pass, ref fail, "Оба сбега наружной резьбы ограничены 14° вращения по фактическому ходу L=P×N.");
            Check("PACKAGING-RUNOUT-INTERNAL-CAPPED-AT-14-DEGREES",
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 8.1, 0.775, 0.155, 1.674, true, true, true, out var internalRunouts, out _) &&
                Math.Abs(internalRunouts.StartLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                Math.Abs(internalRunouts.EndLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                Math.Abs(internalRunouts.TargetTurnFraction * 360.0 - 14.0) < 1e-12 &&
                internalRunouts.IsInternalThread,
                ref pass, ref fail, "Оба сбега внутреннего положительного валика ограничены 14°.");
            Check("PACKAGING-RUNOUT-14-DEGREE-CAP-KEEPS-CORE-ON-SHORT-THREAD",
                PackagingThreadRunoutPolicy.TryResolve(4.05, 2.7, 8.1, 1.125, 0.225, 1.674, true, true, true, out var shortenedRunouts, out _) &&
                Math.Abs(shortenedRunouts.StartLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                Math.Abs(shortenedRunouts.EndLengthMm - 8.1 * 14.0 / 360.0) < 1e-9 &&
                shortenedRunouts.CoreLengthMm >= 8.1 * PackagingThreadRunoutPolicy.MinimumCoreLeadFraction - 1e-9 &&
                shortenedRunouts.StartSectionCount >= PackagingThreadRunoutPolicy.MinimumSectionCount &&
                shortenedRunouts.EndSectionCount >= PackagingThreadRunoutPolicy.MinimumSectionCount,
                ref pass, ref fail, "Ограничение 14° не отнимает лишнюю длину у рабочего участка короткой резьбы.");
            Check("PACKAGING-RUNOUT-BELOW-7-DEGREE-USEFUL-LIMIT-REJECTED",
                !PackagingThreadRunoutPolicy.TryResolve(2.30, 2.7, 8.1, 1.125, 0.225, 1.674, true, true, true, out _, out _),
                ref pass, ref fail, "Если короткая деталь не оставляет по 7° полезного сбега и минимальный рабочий участок, план отклоняется явно.");
            Check("PACKAGING-RUNOUT-SHORT-END-AVOIDS-OVERDENSE-SECTIONS",
                PackagingThreadRunoutPolicy.GetSectionCount(0.21, 5.4) == 3 &&
                PackagingThreadRunoutPolicy.GetSectionCount(0.21, 5.4) - 1 <= Math.Floor(0.21 / 0.075) &&
                PackagingThreadRunoutPolicy.GetSectionCount(0.42, 5.4) >= 3,
                ref pass, ref fail, "Короткий 14-градусный сбег не получает семь сечений с шагом около 0,035 мм; для длинных сбегов сохраняется частота до 24 сечений на оборот.");
            Check("PACKAGING-RUNOUT-ROUNDED-BLUNT-TIP-WIDTH-ENVELOPE",
                PackagingThreadRunoutPolicy.GetWidthFactor(PackagingThreadRunoutPolicy.TerminalCrestFactor) >= PackagingThreadRunoutPolicy.MinimumRunoutTipWidthFactor &&
                Math.Abs(PackagingThreadRunoutPolicy.GetWidthFactor(PackagingThreadRunoutPolicy.TerminalCrestFactor) -
                    (PackagingThreadRunoutPolicy.MinimumRunoutTipWidthFactor + (1.0 - PackagingThreadRunoutPolicy.MinimumRunoutTipWidthFactor) * Math.Sqrt(PackagingThreadRunoutPolicy.TerminalCrestFactor))) < 1e-12 &&
                PackagingThreadRunoutPolicy.GetWidthFactor(0.25) > 0.25 &&
                PackagingThreadRunoutPolicy.GetWidthFactor(1.0) == 1.0 &&
                PackagingThreadRunoutPolicy.GetWidthFactor(0.04) > PackagingThreadRunoutPolicy.GetWidthFactor(0.01),
                ref pass, ref fail, "Осевой профиль сбега сужает ширину медленнее высоты: конец остаётся притуплённым и плавным, без игольчатого схождения.");
            Check("PACKAGING-AUTO-PITCH-RESTARTS-WHEN-DIAMETER-SHRINKS",
                PackagingThreadGeometryPolicy.ShouldRestartAutoPitchFromDefault(true, 350.0, true, 25.0, 25.0, 22.0, false) &&
                !PackagingThreadGeometryPolicy.ShouldRestartAutoPitchFromDefault(true, 350.0, true, 25.0, 25.0, 22.0, true) &&
                !PackagingThreadGeometryPolicy.ShouldRestartAutoPitchFromDefault(true, 22.0, true, 2.70, 2.70, 350.0, false) &&
                !PackagingThreadGeometryPolicy.ShouldRestartAutoPitchFromDefault(true, 350.0, true, 10.0, 25.0, 22.0, false),
                ref pass, ref fail, "После уменьшения диаметра автоподбор сбрасывает только прежний автоподобранный шаг; введённый вручную шаг имеет приоритет.");
            Check("PACKAGING-RUNOUT-BELOW-MINIMUM-CORE-REJECTED",
                !PackagingThreadRunoutPolicy.TryResolve(2.0, 2.7, 8.1, 1.125, 0.225, 1.674, true, true, true, out _, out _),
                ref pass, ref fail, "Длина меньше минимального рабочего участка по-прежнему отклоняется явно.");
            Check("PACKAGING-RUNOUT-SMOOTHSTEP-ENDPOINTS-AND-MONOTONICITY",
                Math.Abs(PackagingThreadRunoutPolicy.SmoothStep(0.0)) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.SmoothStep(1.0) - 1.0) < 1e-12 &&
                PackagingThreadRunoutPolicy.GetCrestFactor(0.0, true) == PackagingThreadRunoutPolicy.TerminalCrestFactor &&
                Math.Abs(PackagingThreadRunoutPolicy.GetCrestFactor(1.0, true) - 1.0) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetCrestFactor(0.0, false) - 1.0) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetCrestFactor(1.0, false) - PackagingThreadRunoutPolicy.TerminalCrestFactor) < 1e-12 &&
                PackagingThreadRunoutPolicy.GetCrestFactor(0.25, true) < PackagingThreadRunoutPolicy.GetCrestFactor(0.75, true),
                ref pass, ref fail, "Сбег использует smoothstep с нулевым наклоном на обоих концах и монотонно меняет высоту.");
            Check("PACKAGING-RUNOUT-TERMINAL-SECTION-STAYS-ABOVE-CAD-MINIMUM",
                PackagingThreadRunoutPolicy.TryResolve(10.0, 2.7, 2.7, 1.1, 0.22, 1.674, false, true, true, out var terminalDepthPlan, out _) &&
                Math.Abs((1.1 * terminalDepthPlan.TerminalCrestFactorValue + 0.22 * 0.10) - PackagingThreadRunoutPolicy.MinimumRunoutProfileDepthMm) < 1e-9 &&
                terminalDepthPlan.TerminalCrestFactorValue > PackagingThreadRunoutPolicy.TerminalCrestFactor &&
                PackagingThreadGeometryPolicy.TryResolvePcoBellProfileDimensionsMm(
                    1.08 * terminalDepthPlan.TerminalCrestFactorValue, 0.22 * 0.10,
                    1.1 * terminalDepthPlan.TerminalCrestFactorValue, out var terminalBellProfile, out _) &&
                terminalBellProfile.RadiusScale >= PackagingThreadGeometryPolicy.MinPcoFilletScale &&
                Math.Abs(PackagingThreadRunoutPolicy.GetCrestFactor(0.0, true, terminalDepthPlan.TerminalCrestFactorValue) - terminalDepthPlan.TerminalCrestFactorValue) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetCrestFactor(1.0, false, terminalDepthPlan.TerminalCrestFactorValue) - terminalDepthPlan.TerminalCrestFactorValue) < 1e-12,
                ref pass, ref fail, "Конечное сечение сбега адаптивно удерживает суммарную глубину не менее 0,20 мм, чтобы касательные дуги не схлопнулись в микросегменты.");
            Check("PACKAGING-RUNOUT-ROOT-OVERLAP-AND-SMOOTH-FADE",
                Math.Abs(PackagingThreadRunoutPolicy.GetRootFactor(0.0, true, 0.05) - 0.30) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetRootFactor(1.0, true, 0.05) - 1.0) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetRootFactor(0.0, false, 0.05) - 1.0) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetRootFactor(1.0, false, 0.05) - 0.30) < 1e-12 &&
                Math.Abs(PackagingThreadRunoutPolicy.GetRootFactor(0.5, true, 0.155) - 0.55) < 1e-12 &&
                PackagingThreadRunoutPolicy.GetRootFactor(0.25, true, 0.155) < PackagingThreadRunoutPolicy.GetRootFactor(0.75, true, 0.155) &&
                PackagingThreadRunoutPolicy.GetRootFactor(0.25, false, 0.155) > PackagingThreadRunoutPolicy.GetRootFactor(0.75, false, 0.155),
                ref pass, ref fail, "Корень сохраняет ограниченное проникновение и затухает по smoothstep с нулевым наклоном на обоих концах.");
            Check("PACKAGING-VENT-SLOT-DEFAULTS-ARE-TWO-FOR-BOTH-TYPES",
                PackagingThreadVentSlotPolicy.GetDefaultCount(false) == 2 &&
                PackagingThreadVentSlotPolicy.GetDefaultCount(true) == 2 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetDefaultWidthDegrees(false) - 14.0) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetDefaultWidthDegrees(true) - 14.0) < 1e-12,
                ref pass, ref fail, "Both external and internal variants default to two channels with 14-degree width.");
            Check("PACKAGING-VENT-SLOT-CUTTER-START-AND-END-OVERRUN",
                Math.Abs(PackagingThreadVentSlotPolicy.GetStartOverrunMm(2.025) - 2.275) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetAxialOverrunMm(0.4) - 1.0) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetAxialOverrunMm(8.1) - 8.1) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetEndOverrunMm(8.1, 40.0) - 8.1) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetEndOverrunMm(8.1, 2.0) - 2.25) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetEndOverrunMm(8.1, 0.0) - 0.25) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetVentSlotEndOverrunMm(40.0) - 39.98) < 1e-12 &&
                Math.Abs(PackagingThreadVentSlotPolicy.GetVentSlotEndOverrunMm(0.0)) < 1e-12,
                ref pass, ref fail, "Legacy relief overrun remains unchanged; the new vent-slot end policy stops 0.02 mm inside the cylindrical junction and never overruns when no span remains.");
            Check("PACKAGING-VENT-SLOT-OUTER-PLAN-THREE-CHANNELS",
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 14.0, out var ventOuter, out _) &&
                ventOuter.Enabled && ventOuter.SlotCount == 3 && Math.Abs(ventOuter.SpacingDegrees - 120.0) < 1e-12 &&
                Math.Abs(ventOuter.GetStartAngleDegrees(0) - 7.0) < 1e-12 &&
                Math.Abs(ventOuter.GetStartAngleDegrees(1) - 127.0) < 1e-12 &&
                Math.Abs(ventOuter.GetStartAngleDegrees(2) - 247.0) < 1e-12,
                ref pass, ref fail, "Базовый план использует три канала с шириной 14°; окончательные центры пересчитываются по фазам начала/конца.");
            Check("PACKAGING-VENT-SLOT-INNER-PLAN-THREE-CHANNELS",
                PackagingThreadVentSlotPolicy.TryResolve(true, true, 3, 28.0, out var ventInner, out _) &&
                ventInner.Enabled && ventInner.SlotCount == 3 && Math.Abs(ventInner.SpacingDegrees - 120.0) < 1e-12 &&
                Math.Abs(ventInner.GetStartAngleDegrees(0) - 14.0) < 1e-12 &&
                Math.Abs(ventInner.GetStartAngleDegrees(2) - 254.0) < 1e-12,
                ref pass, ref fail, "Для внутренней и наружной упаковочной резьбы действует одинаковый лимит в три канала; ширина остаётся параметром.");
            Check("PACKAGING-VENT-SLOT-DISABLED-PRESERVES-NO-SLOTS",
                PackagingThreadVentSlotPolicy.TryResolve(false, true, 0, double.NaN, out var noVentSlots, out _) &&
                !noVentSlots.Enabled && noVentSlots.SlotCount == 0,
                ref pass, ref fail, "При выключенном чекбоксе политика не создаёт пропилов и не валидирует неиспользуемые поля.");
            Check("PACKAGING-VENT-SLOT-ACCEPTS-COUNT-ONE-TO-THREE",
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 1, 14.0, out var oneVentSlot, out _) && oneVentSlot.SlotCount == 1 &&
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 2, 14.0, out var twoVentSlots, out _) && twoVentSlots.SlotCount == 2 &&
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 14.0, out var threeVentSlots, out _) && threeVentSlots.SlotCount == 3 &&
                !PackagingThreadVentSlotPolicy.TryResolve(true, false, 0, 14.0, out _, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolve(true, false, 4, 14.0, out _, out _),
                ref pass, ref fail, "Разрешено выбрать от одного до трёх вентиляционных каналов; ноль и значения выше трёх отклоняются.");
            Check("PACKAGING-VENT-SLOT-REJECTS-OVERLAPPING-WIDTH",
                !PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 120.0, out _, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 0.0, out _, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolve(true, false, 4, 10.0, out _, out _),
                ref pass, ref fail, "Ширина должна быть положительной и меньше углового интервала; недопустимое число каналов отклоняется.");
            string[] ventSlotErrorResourceKeys = {
                "PackagingThreadDialog.VentSlot.Error.CountRange",
                "PackagingThreadDialog.VentSlot.Error.WidthRange",
                "PackagingThreadDialog.VentSlot.Error.WidthMustBeBelowSpacing",
                "PackagingThreadDialog.VentSlot.Error.EndpointParametersInvalid",
                "PackagingThreadDialog.VentSlot.Error.EndpointPhaseNotFinite",
                "PackagingThreadDialog.VentSlot.Error.NoClearSector",
                "PackagingThreadDialog.VentSlot.Error.UnsafeEndpointSpan",
                "PackagingThreadDialog.VentSlot.Error.UnsafePlacementVerificationFailed"
            };
            bool ventSlotErrorsLocalized = russianCatalog != null && englishCatalog != null && ventSlotErrorResourceKeys.All(key =>
                !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("PACKAGING-VENT-SLOT-ERROR-RESOURCES-RU-EN-PARITY",
                ventSlotErrorsLocalized, ref pass, ref fail,
                "Every typed vent-slot policy failure has an RU/EN resource and English values contain no Cyrillic.");

            string[] packagingPolicyFailureResourceKeys = {
                "PackagingThreadPolicy.DiameterRange", "PackagingThreadPolicy.PitchMinimum",
                "PackagingThreadPolicy.StartsRange", "PackagingThreadPolicy.HeightRatioRange",
                "PackagingThreadPolicy.ClearanceRange", "PackagingThreadPolicy.WallThicknessUnavailable",
                "PackagingThreadPolicy.ProfileHeightBelowMinimum", "PackagingThreadPolicy.ProfileDepthExceedsPitch",
                "PackagingThreadPolicy.WallBelowSafetyMinimum", "PackagingThreadPolicy.ExternalThreadWallInsufficient",
                "PackagingThreadPolicy.InternalClearanceWallInsufficient", "PackagingThreadPolicy.InternalEmbedExceedsHalfWall",
                "PackagingThreadPolicy.PcoProfileDimensionsInvalid", "PackagingThreadPolicy.PcoProfileCannotFit",
                "PackagingThreadPolicy.PcoFilletsTooSmall", "PackagingThreadPolicy.PcoFlanksTooShort",
                "PackagingThreadPolicy.PcoCrownLandInsufficient", "PackagingThreadPolicy.AutoPitch.StartPitchMinimum",
                "PackagingThreadPolicy.AutoPitch.LengthPositive", "PackagingThreadPolicy.AutoPitch.TurnsBelowMinimum",
                "PackagingThreadPolicy.AutoPitch.FixedLengthPitchTooLarge", "PackagingThreadPolicy.AutoPitch.NoCandidates",
                "PackagingThreadPolicy.AutoPitch.HeightWouldBeReduced", "PackagingThreadPolicy.AutoPitch.ProfileHeightTooSmallForFillets",
                "PackagingThreadPolicy.AutoPitch.ProfileBaseWidthInsufficient", "PackagingThreadPolicy.AutoPitch.CrownLandInsufficient",
                "PackagingThreadPolicy.Runout.InvalidDimensions", "PackagingThreadPolicy.Runout.LengthBelowMinimumCore",
                "PackagingThreadPolicy.Runout.LengthTooShortForRunouts", "PackagingThreadPolicy.Runout.CoreTooShortAfterRunouts",
                "PackagingThreadPolicy.Runout.TerminalCrestInvalid"
            };
            bool packagingPolicyResourcesComplete = russianCatalog != null && englishCatalog != null &&
                packagingPolicyFailureResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("PACKAGING-POLICY-FAILURE-RESOURCES-HAVE-RU-EN-DETAILS",
                packagingPolicyResourcesComplete, ref pass, ref fail,
                "All typed cap/neck auto-pitch validation failures must resolve to complete RU/EN resources, with no English Cyrillic fallback.");

            PackagingThreadVentSlotFailure typedCountFailure;
            PackagingThreadVentSlotFailure typedWidthFailure;
            PackagingThreadVentSlotFailure typedPlacementFailure;
            PackagingThreadVentSlotPolicy.Plan ignoredVentPlan;
            bool typedVentFailuresStable =
                !PackagingThreadVentSlotPolicy.TryResolveWithFailure(true, false, 0, 14.0, out ignoredVentPlan, out typedCountFailure) &&
                typedCountFailure != null && typedCountFailure.Code == PackagingThreadVentSlotFailureCode.SlotCountOutOfRange &&
                typedCountFailure.ResourceKey == "PackagingThreadDialog.VentSlot.Error.CountRange" &&
                typedCountFailure.FormatArguments.Length == 2 && !string.IsNullOrWhiteSpace(typedCountFailure.DiagnosticDetail) &&
                !PackagingThreadVentSlotPolicy.TryResolveWithFailure(true, false, 3, 120.0, out ignoredVentPlan, out typedWidthFailure) &&
                typedWidthFailure != null && typedWidthFailure.Code == PackagingThreadVentSlotFailureCode.WidthMustBeBelowSpacing &&
                typedWidthFailure.ResourceKey == "PackagingThreadDialog.VentSlot.Error.WidthMustBeBelowSpacing" &&
                typedWidthFailure.FormatArguments.Length == 3 && !string.IsNullOrWhiteSpace(typedWidthFailure.DiagnosticDetail) &&
                PackagingThreadVentSlotPolicy.TryResolveWithFailure(true, false, 3, 14.0, out var typedBasePlan, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlanWithFailure(typedBasePlan, new[] { 0.0, 60.0 }, out ignoredVentPlan, out typedPlacementFailure) &&
                typedPlacementFailure != null && typedPlacementFailure.Code == PackagingThreadVentSlotFailureCode.UnsafeEndpointSpan &&
                typedPlacementFailure.ResourceKey == "PackagingThreadDialog.VentSlot.Error.UnsafeEndpointSpan" &&
                typedPlacementFailure.FormatArguments.Length == 3 && !string.IsNullOrWhiteSpace(typedPlacementFailure.DiagnosticDetail) &&
                PackagingThreadVentSlotPolicy.TryResolveWithFailure(true, true, 2, 14.0, out var d326TwoVentPlan, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlanWithFailure(d326TwoVentPlan,
                    new[] { 0.0, 90.0, 180.0, 270.0 }, out ignoredVentPlan, out var d326EndpointFailure) &&
                d326EndpointFailure != null && d326EndpointFailure.Code == PackagingThreadVentSlotFailureCode.UnsafeEndpointSpan &&
                d326EndpointFailure.ResourceKey == "PackagingThreadDialog.VentSlot.Error.UnsafeEndpointSpan" &&
                d326EndpointFailure.FormatArguments.Length == 3 &&
                Math.Abs(Convert.ToDouble(d326EndpointFailure.FormatArguments[1], CultureInfo.InvariantCulture) - 90.0) < 1e-8 &&
                Math.Abs(Convert.ToDouble(d326EndpointFailure.FormatArguments[2], CultureInfo.InvariantCulture) - 83.0) < 1e-8;
            Check("PACKAGING-VENT-SLOT-FAILURES-USE-STABLE-CODES-AND-ARGS",
                typedVentFailuresStable, ref pass, ref fail,
                "Policy callers can branch on stable failure codes and localize with resource keys/format arguments; diagnostic details are separate.");

            Check("PACKAGING-VENT-SLOT-ENDPOINT-AWARE-UNIFORM-PHASE-PLAN",
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 14.0, out var baseThreeSlots, out _) &&
                PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlan(baseThreeSlots, new[] { 0.0, 73.0 }, out var phaseAwareSlots, out _) &&
                phaseAwareSlots.CenterAnglesDegrees.Length == 3 &&
                Math.Abs(phaseAwareSlots.CenterAnglesDegrees[1] - phaseAwareSlots.CenterAnglesDegrees[0] - 120.0) < 1e-9 &&
                Math.Abs(phaseAwareSlots.CenterAnglesDegrees[2] - phaseAwareSlots.CenterAnglesDegrees[1] - 120.0) < 1e-9 &&
                Math.Abs(360.0 - phaseAwareSlots.CenterAnglesDegrees[2] + phaseAwareSlots.CenterAnglesDegrees[0] - 120.0) < 1e-9 &&
                Math.Abs(phaseAwareSlots.CenterAnglesDegrees[0] - 36.5) < 1e-8,
                ref pass, ref fail, "Three channels keep exactly 120-degree spacing; the complete pattern rotates to maximize safe clearance from the supplied endpoint phases.");
            Check("PACKAGING-VENT-SLOT-REJECTS-UNSAFE-UNIFORM-PHASE",
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 3, 14.0, out var baseUnsafeThreeSlots, out _) &&
                !PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlan(baseUnsafeThreeSlots, new[] { 0.0, 60.0 }, out _, out var unsafeUniformError) &&
                !string.IsNullOrWhiteSpace(unsafeUniformError),
                ref pass, ref fail, "If no uniform channel phase can maintain the safety margin around all thread endpoints, the layout is rejected instead of leaving small remnants.");
            Check("PACKAGING-VENT-SLOT-ENDPOINT-AWARE-ONE-AND-TWO-CHANNELS",
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 1, 14.0, out var baseOneSlot, out _) &&
                PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlan(baseOneSlot, new[] { 0.0, 73.0 }, out var phaseAwareOneSlot, out _) &&
                phaseAwareOneSlot.CenterAnglesDegrees.Length == 1 &&
                PackagingThreadVentSlotPolicy.TryResolve(true, false, 2, 14.0, out var baseTwoSlots, out _) &&
                PackagingThreadVentSlotPolicy.TryResolveEndpointAwarePlan(baseTwoSlots, new[] { 0.0, 73.0 }, out var phaseAwareTwoSlots, out _) &&
                phaseAwareTwoSlots.CenterAnglesDegrees.Length == 2 &&
                Math.Abs(phaseAwareTwoSlots.CenterAnglesDegrees[1] - phaseAwareTwoSlots.CenterAnglesDegrees[0] - 180.0) < 1e-9,
                ref pass, ref fail, "One to three channels use a common angular offset while preserving equal intervals.");
            bool ventSlotEditHasEndpointAndFeatureCountDiagnostics =
                packagingThreadBuilderSource.Contains("vent-slot-endpoint-aware-input") &&
                packagingThreadBuilderSource.Contains("vent-slot-endpoint-aware-rejected") &&
                packagingThreadBuilderSource.Contains("vent-slot-cuts-complete") &&
                packagingThreadBuilderSource.Contains("createdVentSlots.Count != ventSlotPlan.SlotCount") &&
                packagingThreadBuilderSource.Contains("PackagingThreadGeometryBuilder.Error.VentSlotCreationFailed");
            Check("PACKAGING-VENT-SLOT-EDIT-LOGS-HANDEDNESS-ENDPOINTS-AND-REQUIRES-ALL-CUTS",
                ventSlotEditHasEndpointAndFeatureCountDiagnostics, ref pass, ref fail,
                "Record the actual RH/LH and edge-direction phase inputs, log endpoint-placement rejection, and refuse to commit if the number of verified Revolve cuts differs from the requested channel count.");
            Check("PACKAGING-CLEARANCE-DIAMETRAL-COMPENSATION-PAIR",
                PackagingThreadGeometryPolicy.TryResolveCompensatedCylinderRadiusMm(11.0, 1.1, 0.20, false, out var externalCompensatedRadius, out _) &&
                PackagingThreadGeometryPolicy.TryResolveCompensatedCylinderRadiusMm(11.0, 1.1, 0.20, true, out var internalCompensatedRadius, out _) &&
                Math.Abs(externalCompensatedRadius - 9.9) < 1e-9 &&
                Math.Abs(internalCompensatedRadius - 11.1) < 1e-9,
                ref pass, ref fail, "Основа наружного валика уменьшается на H по радиусу; внутреннее отверстие расширяется на C/2 радиально.");

            Check("PACKAGING-MATING-DIAMETERS-D22-H1.1-C0.2",
                PackagingThreadGeometryPolicy.TryResolveMatingDiametersMm(22.0, 1.1, 0.2,
                    out var extCrestD, out var extRootD, out var intRootD, out var intCrestD, out _) &&
                Math.Abs(extCrestD - 22.0) < 1e-9 &&
                Math.Abs(extRootD - 19.8) < 1e-9 &&
                Math.Abs(intRootD - 22.2) < 1e-9 &&
                Math.Abs(intCrestD - 20.0) < 1e-9 &&
                Math.Abs((intRootD - extCrestD) - 0.2) < 1e-9 &&
                Math.Abs((intCrestD - extRootD) - 0.2) < 1e-9,
                ref pass, ref fail, "Для D=22, H=1,1, C=0,2: наружная Ø22,0/Ø19,8, внутренняя Ø22,2/Ø20,0; диаметральный зазор на обеих границах равен C.");

            Check("PACKAGING-EXTERNAL-POSITIVE-RIB-CORE-IS-MINOR-DIAMETER",
                PackagingThreadGeometryPolicy.TryResolveCompensatedCylinderRadiusMm(11.0, 1.1, 0.20, false, out var externalStockRadius, out _) &&
                PackagingThreadGeometryPolicy.TryResolveProfileRadiiMm(externalStockRadius, 1.1, 0.22, false, out var externalRootEdge, out var externalCrest, out _) &&
                Math.Abs(externalStockRadius * 2.0 - 19.8) < 1e-9 &&
                externalRootEdge < externalStockRadius && externalCrest > externalStockRadius &&
                Math.Abs(externalCrest * 2.0 - 22.0) < 1e-9,
                ref pass, ref fail, "Наружный валик добавляется к уменьшенной основе Ø19,8 и достигает номинальной вершины Ø22,0; операция не режет канавку в исходной основе.");

            Check("PACKAGING-INTERNAL-CLEARANCE-RESPECTS-MINIMUM-WALL",
                !PackagingThreadGeometryPolicy.TryResolve(22, 2.7, 1, 0.025, 0.5, 0.6, true, out _, out _),
                ref pass, ref fail, "Расширение отверстия на C/2 радиально не должно оставлять стенку тоньше 0,6 мм.");
            Check("PACKAGING-THIN-WALL-REJECT",
                !PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 1, 0.025, 0.0, 0.4, true, out _, out _),
                ref pass, ref fail, "Предварительная политика закрывается при стенке тоньше 0,6 мм.");
            Check("PACKAGING-INVALID-STARTS-REJECT",
                !PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 0, 0.025, 0.0, 1.0, false, out _, out _) &&
                !PackagingThreadGeometryPolicy.TryResolve(28, 2.7, 13, 0.025, 0.0, 1.0, false, out _, out _),
                ref pass, ref fail, "Ноль и больше 12 заходов отклоняются.");
            Check("PACKAGING-INVALID-PITCH-REJECT",
                !PackagingThreadGeometryPolicy.TryResolve(28, 0.1, 1, 0.025, 0.0, 10.0, false, out _, out _) &&
                PackagingThreadGeometryPolicy.TryResolve(28, 13.0, 1, 0.025, 0.0, 10.0, false, out var largePitch, out _) &&
                Math.Abs(largePitch.PitchMm - 13.0) < 1e-12,
                ref pass, ref fail, "Шаг ниже 0,25 мм отклоняется, а значение выше прежних 12 мм допустимо при согласованной геометрии.");
            Check("PACKAGING-INVALID-RATIO-REJECT",
                !PackagingThreadGeometryPolicy.TryResolve(28, 1.0, 1, 0.001, 0.0, 1.0, false, out _, out _) &&
                !PackagingThreadGeometryPolicy.TryResolve(28, 1.0, 1, 0.1, 0.0, 1.0, false, out _, out _),
                ref pass, ref fail, "Отношение высоты вне безопасного диапазона отклоняется.");
            Check("PACKAGING-INVALID-CLEARANCE-REJECT",
                !PackagingThreadGeometryPolicy.TryResolve(28, 1.0, 1, 0.025, -0.1, 1.0, false, out _, out _) &&
                !PackagingThreadGeometryPolicy.TryResolve(28, 1.0, 1, 0.025, 0.6, 1.0, false, out _, out _),
                ref pass, ref fail, "Отрицательный и чрезмерный зазор печати отклоняются.");
            Check("PACKAGING-TURNS-USE-LEAD",
                PackagingThreadGeometryPolicy.TryGetTurns(10.0, 5.4, out double packTurns) &&
                Math.Abs(packTurns - (10.0 / 5.4)) < 1e-12,
                ref pass, ref fail, "Число оборотов рассчитывается по lead, а не по pitch.");
            Check("PACKAGING-TURNS-INVALID-REJECT",
                !PackagingThreadGeometryPolicy.TryGetTurns(0.0, 2.0, out _) &&
                !PackagingThreadGeometryPolicy.TryGetTurns(10.0, 0.0, out _),
                ref pass, ref fail, "Нулевая длина и нулевой lead отклоняются.");
            Check("PACKAGING-START-PHASES-EVENLY-SPACED",
                Math.Abs(PackagingThreadGeometryPolicy.GetStartPhaseDegrees(0, 3) - 0.0) < 1e-12 &&
                Math.Abs(PackagingThreadGeometryPolicy.GetStartPhaseDegrees(1, 3) - 120.0) < 1e-12 &&
                Math.Abs(PackagingThreadGeometryPolicy.GetStartPhaseDegrees(2, 3) - 240.0) < 1e-12,
                ref pass, ref fail, "Начальные фазы заходов распределяются равномерно по окружности.");
            double[] packRadial0 = PackagingThreadPhaseMath.RotateAroundAxis(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 0.0);
            double[] packRadial1 = PackagingThreadPhaseMath.RotateAroundAxis(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 2.0 * Math.PI / 3.0);
            double[] packRadial2 = PackagingThreadPhaseMath.RotateAroundAxis(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 }, 4.0 * Math.PI / 3.0);
            double phaseDot01 = packRadial0[0] * packRadial1[0] + packRadial0[1] * packRadial1[1] + packRadial0[2] * packRadial1[2];
            double phaseDot12 = packRadial1[0] * packRadial2[0] + packRadial1[1] * packRadial2[1] + packRadial1[2] * packRadial2[2];
            double phaseDot20 = packRadial2[0] * packRadial0[0] + packRadial2[1] * packRadial0[1] + packRadial2[2] * packRadial0[2];
            Check("PACKAGING-START-PHASE-VECTORS-ROTATE-AROUND-AXIS",
                Math.Abs(phaseDot01 + 0.5) < 1e-12 && Math.Abs(phaseDot12 + 0.5) < 1e-12 && Math.Abs(phaseDot20 + 0.5) < 1e-12 &&
                Math.Abs(Math.Sqrt(packRadial0[0] * packRadial0[0] + packRadial0[1] * packRadial0[1] + packRadial0[2] * packRadial0[2]) - 1.0) < 1e-12 &&
                Math.Abs(Math.Sqrt(packRadial1[0] * packRadial1[0] + packRadial1[1] * packRadial1[1] + packRadial1[2] * packRadial1[2]) - 1.0) < 1e-12 &&
                Math.Abs(Math.Sqrt(packRadial2[0] * packRadial2[0] + packRadial2[1] * packRadial2[1] + packRadial2[2] * packRadial2[2]) - 1.0) < 1e-12,
                ref pass, ref fail, "Для трёх заходов радиальные направления повернуты на 0/120/240° без изменения длины вектора.");
            Check("PACKAGING-RH-LH-CLOCKWISE-MAPPING-INDEPENDENT-OF-EDGE",
                !PackagingThreadPhaseMath.ResolveClockwiseRotation(true) &&
                PackagingThreadPhaseMath.ResolveClockwiseRotation(false),
                ref pass, ref fail, "Based on the Inventor result, RH maps to ClockwiseRotation=false and LH to true; selected end changes axial build direction only.");
            Check("PACKAGING-RH-HIGH-END-AND-LH-LOW-END-DO-NOT-COLLAPSE-TO-SAME-CLOCKWISE-FLAG",
                PackagingThreadPhaseMath.ResolveClockwiseRotation(true) !=
                PackagingThreadPhaseMath.ResolveClockwiseRotation(false),
                ref pass, ref fail, "RH and LH requests that previously both produced ClockwiseRotation=false now differ.");
            string packNameA = PackagingThreadFeatureNamePolicy.CreateSafeUniqueName(1, 3, 0.0, Guid.Parse("11111111-1111-1111-1111-111111111111"));
            string packNameB = PackagingThreadFeatureNamePolicy.CreateSafeUniqueName(1, 3, 0.0, Guid.Parse("22222222-2222-2222-2222-222222222222"));
            Check("PACKAGING-FEATURE-NAMES-ARE-SAFE-AND-UNIQUE",
                packNameA != packNameB && packNameA.StartsWith("HVPkg_", StringComparison.Ordinal) &&
                packNameA.IndexOf('/') < 0 && packNameA.IndexOf('\\') < 0 && packNameA.IndexOf('°') < 0 &&
                packNameA.IndexOf('—') < 0 && packNameA.IndexOf(' ') < 0,
                ref pass, ref fail, "Feature labels use conservative ASCII and differ for separate builds, preventing opposite-end name collisions.");

            Check("TAPER-GOST6211-JIS-CARRIER-NOMINAL-SIZE-ALIASES",
                PipeThreadNominalSizePolicy.AreEquivalent("1/2", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT1/2", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT 1/2", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("R 1/2", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("Rc1/2", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("1/2 in", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("1/2\"", "0.5") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT 1 1/4", "1.25") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT 1-1/4", "1.25") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT ½", "1/2") &&
                PipeThreadNominalSizePolicy.AreEquivalent("PT 1½", "1.5") &&
                !PipeThreadNominalSizePolicy.AreEquivalent("PT 3/4", "1/2") &&
                !PipeThreadNominalSizePolicy.AreEquivalent("PT 1/2", "3/4") &&
                !PipeThreadNominalSizePolicy.AreEquivalent("unknown", "1/2"),
                ref pass, ref fail, "GOST 6211 carrier resolver сравнивает числовой номинал, а не локализованные строки PT/R/Rc/inch из Inventor Thread.xls; несовпадающий размер отклоняется.");
            Check("TAPER-TABLE-ALIAS-JIS-RU-EN-CLASSIFICATION",
                TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS Taper External", true) &&
                !TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS Taper External", false) &&
                TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS коническая наружная", true) &&
                !TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS коническая внутренняя", true) &&
                TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS коническая внутренняя", false) &&
                !TaperThreadTableAliasPolicy.IsCompatibleJisTaperType("JIS cylindrical external", true),
                ref pass, ref fail, "Localized Thread.xls aliases are interpreted in one data policy; external/internal compatibility remains identical for English and Russian names.");
            Check("TAPER-TABLE-ALIAS-NPT-LEGACY-FALLBACK",
                TaperThreadTableAliasPolicy.IsExactNptType(" NPT ") &&
                TaperThreadTableAliasPolicy.IsNptFallbackType("NPT") &&
                TaperThreadTableAliasPolicy.IsNptFallbackType("NPT ANSI") &&
                !TaperThreadTableAliasPolicy.IsNptFallbackType("NPT PVC") &&
                !TaperThreadTableAliasPolicy.IsNptFallbackType("ANSI NPT"),
                ref pass, ref fail, "The exact NPT preference and legacy prefix fallback are preserved, including exclusion of PVC types.");
            string[] taperTableErrorKeys = {
                "TaperedThreadTableResolver.StandardNotSelected",
                "TaperedThreadTableResolver.EnumerationFailed",
                "TaperedThreadTableResolver.NoCompatibleType",
                "TaperedThreadTableResolver.DesignationNotFound",
                "TaperedThreadTableResolver.DesignationNotTapered",
                "TaperedThreadTableResolver.MetadataReadFailed",
                "TaperedThreadTableResolver.DesignationPropertiesMismatch"
            };
            bool taperTableResourceParity = russianCatalog != null && englishCatalog != null && taperTableErrorKeys.All(key =>
                !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                !englishCatalog.GetString(key, CultureInfo.InvariantCulture).Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("TAPER-TABLE-ERROR-RESOURCES-HAVE-RU-EN-PARITY", taperTableResourceParity,
                ref pass, ref fail, "Table enumeration/type/designation failures have complete explicit RU/EN resources without English fallback to Cyrillic.");
            string noCompatibleRu = russianCatalog == null ? null : russianCatalog.GetString("TaperedThreadTableResolver.NoCompatibleType", CultureInfo.InvariantCulture);
            string noCompatibleEn = englishCatalog == null ? null : englishCatalog.GetString("TaperedThreadTableResolver.NoCompatibleType", CultureInfo.InvariantCulture);
            string designationRu = russianCatalog == null ? null : russianCatalog.GetString("TaperedThreadTableResolver.DesignationNotFound", CultureInfo.InvariantCulture);
            string designationEn = englishCatalog == null ? null : englishCatalog.GetString("TaperedThreadTableResolver.DesignationNotFound", CultureInfo.InvariantCulture);
            string notTaperedRu = russianCatalog == null ? null : russianCatalog.GetString("TaperedThreadTableResolver.DesignationNotTapered", CultureInfo.InvariantCulture);
            string notTaperedEn = englishCatalog == null ? null : englishCatalog.GetString("TaperedThreadTableResolver.DesignationNotTapered", CultureInfo.InvariantCulture);
            string metadataFailedRu = russianCatalog == null ? null : russianCatalog.GetString("TaperedThreadTableResolver.MetadataReadFailed", CultureInfo.InvariantCulture);
            string metadataFailedEn = englishCatalog == null ? null : englishCatalog.GetString("TaperedThreadTableResolver.MetadataReadFailed", CultureInfo.InvariantCulture);
            string propertiesMismatchRu = russianCatalog == null ? null : russianCatalog.GetString("TaperedThreadTableResolver.DesignationPropertiesMismatch", CultureInfo.InvariantCulture);
            string propertiesMismatchEn = englishCatalog == null ? null : englishCatalog.GetString("TaperedThreadTableResolver.DesignationPropertiesMismatch", CultureInfo.InvariantCulture);
            bool formatArgsRender = noCompatibleRu != null && noCompatibleEn != null && designationRu != null && designationEn != null &&
                notTaperedRu != null && notTaperedEn != null && metadataFailedRu != null && metadataFailedEn != null &&
                propertiesMismatchRu != null && propertiesMismatchEn != null &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), noCompatibleRu, "наружная", "JIS Taper").Contains("наружная") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), noCompatibleEn, "external", "JIS Taper").Contains("external") &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), designationRu, "1/2 PT", "JIS Taper").Contains("1/2 PT") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), designationEn, "1/2 PT", "JIS Taper").Contains("1/2 PT") &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), notTaperedRu, "1/2 PT", "JIS Taper").Contains("1/2 PT") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), notTaperedEn, "1/2 PT", "JIS Taper").Contains("1/2 PT") &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), metadataFailedRu, "1/2 PT", "JIS Taper").Contains("JIS Taper") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), metadataFailedEn, "1/2 PT", "JIS Taper").Contains("JIS Taper") &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), propertiesMismatchRu, "1/2 PT", "JIS Taper").Contains("JIS Taper") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), propertiesMismatchEn, "1/2 PT", "JIS Taper").Contains("JIS Taper");
            Check("TAPER-TABLE-ERROR-RESOURCE-ARGUMENTS-RENDER-IN-RU-EN", formatArgsRender,
                ref pass, ref fail, "Parameterized table failures format standard/direction/designation values from arguments in the selected language rather than embedding diagnostics in the message.");
            Check("TAPER-TABLE-ERROR-CODES-ARE-APPENDED-STABLY",
                (int)HelivantaErrorCode.TaperedTableTypeResolutionFailed == 8 &&
                (int)HelivantaErrorCode.TaperedTableDesignationNotFound == 9 &&
                (int)HelivantaErrorCode.TaperedTableDesignationTypeMismatch == 10 &&
                (int)HelivantaErrorCode.TaperedHostLengthValidationFailed == 7,
                ref pass, ref fail, "New table-resolution codes are appended without renumbering previously persisted/diagnosed operation codes.");

            // Optional Inventor ThreadInfo members may be absent in some Thread.xls variants.
            // Only explicit dispatch/member-not-found errors may trigger fallback; arbitrary COM
            // failures and malformed values must continue to fail closed.
            bool optionalComMissingMemberCodes =
                OptionalComPropertyPolicy.IsMemberUnavailable(new COMException("missing", OptionalComPropertyPolicy.DispEMemberNotFound)) &&
                OptionalComPropertyPolicy.IsMemberUnavailable(new COMException("unknown name", OptionalComPropertyPolicy.DispEUnknownName)) &&
                OptionalComPropertyPolicy.IsMemberUnavailable(new COMException("type member missing", OptionalComPropertyPolicy.TypeEElementNotFound));
            Check("THREADINFO-OPTIONAL-PROPERTY-RECOGNIZES-ONLY-KNOWN-MISSING-MEMBER-HRESULTS",
                optionalComMissingMemberCodes, ref pass, ref fail,
                "Dispatch member-not-found HRESULTs identify supported optional-property absence.");

            bool optionalComMissingMemberNested = OptionalComPropertyPolicy.IsMemberUnavailable(
                new InvalidOperationException("wrapped dispatch failure",
                    new COMException("member missing", OptionalComPropertyPolicy.DispEMemberNotFound)));
            Check("THREADINFO-OPTIONAL-PROPERTY-UNWRAPS-NESTED-COM-MEMBER-ABSENCE",
                optionalComMissingMemberNested, ref pass, ref fail,
                "The classifier recognizes a known missing-member HRESULT preserved in an inner COM exception.");

            bool optionalComOtherFailuresRemainFatal =
                !OptionalComPropertyPolicy.IsMemberUnavailable(new COMException("E_FAIL", unchecked((int)0x80004005))) &&
                !OptionalComPropertyPolicy.IsMemberUnavailable(new COMException("E_NOTIMPL", unchecked((int)0x80004001))) &&
                !OptionalComPropertyPolicy.IsMemberUnavailable(new FormatException("malformed numeric table value")) &&
                !OptionalComPropertyPolicy.IsMemberUnavailable(new InvalidOperationException("COM transport failure"));
            Check("THREADINFO-OPTIONAL-PROPERTY-DOES-NOT-SWALLOW-OTHER-COM-OR-CONVERSION-FAILURES",
                optionalComOtherFailuresRemainFatal, ref pass, ref fail,
                "Only explicit member absence allows fallback; generic COM failures and malformed property values remain fatal.");

            string taperedBuilderForOptionalProperties = File.ReadAllText(Path.Combine(sourceRoot, "NativeTaperedThreadBuilder.cs"));
            bool taperedOptionalPropertyReadsAreNarrow =
                taperedBuilderForOptionalProperties.Contains("TryReadOptionalThreadInfoProperty(() => (object)native.Pitch, \"Pitch\"") &&
                taperedBuilderForOptionalProperties.Contains("TryReadOptionalThreadInfoProperty(() => (object)native.ThreadHeight, \"ThreadHeight\"") &&
                taperedBuilderForOptionalProperties.Contains("catch (Exception ex) when (OptionalComPropertyPolicy.IsMemberUnavailable(ex))") &&
                !taperedBuilderForOptionalProperties.Contains("try { pitchValue = Convert.ToDouble(native.Pitch") &&
                !taperedBuilderForOptionalProperties.Contains("try { tableValue = native.OutsidePipeDiameter;");
            Check("TAPERED-BUILDER-OPTIONAL-THREADINFO-PROPERTIES-USE-NARROW-COM-FALLBACK",
                taperedOptionalPropertyReadsAreNarrow, ref pass, ref fail,
                "Optional table properties use a shared missing-member classifier; unexpected COM failures are not swallowed.");

            string nativeTaperedBuilderInterfaceSource = File.ReadAllText(Path.Combine(sourceRoot, "NativeTaperedThreadBuilder.cs"), Encoding.UTF8);
            string nativeConversionInterfaceSource = File.ReadAllText(Path.Combine(sourceRoot, "NativeThreadConversionService.cs"), Encoding.UTF8);
            bool taperedComInterfaceIsHandledIndependently =
                nativeTaperedBuilderInterfaceSource.Contains("private static TaperedThreadInfo ResolveThreadInfo(") &&
                nativeTaperedBuilderInterfaceSource.Contains("object createdInfo = null;") &&
                nativeTaperedBuilderInterfaceSource.Contains("info = createdInfo as TaperedThreadInfo;") &&
                nativeTaperedBuilderInterfaceSource.Contains("TryGetNativePitchAndDepthMm(object info,") &&
                nativeTaperedBuilderInterfaceSource.Contains("TryResolveTaperRadii(object info,") &&
                !System.Text.RegularExpressions.Regex.IsMatch(nativeTaperedBuilderInterfaceSource, "(?<!Tapered)ThreadInfo\\s+info\\s*=\\s*ResolveThreadInfo") &&
                !nativeConversionInterfaceSource.Contains("ThreadInfo rawInfo = feature.ThreadInfo as ThreadInfo;") &&
                !nativeConversionInterfaceSource.Contains("ThreadInfo rawInfo = feature == null ? null : feature.ThreadInfo as ThreadInfo;") &&
                nativeConversionInterfaceSource.Contains("object rawInfo = feature.ThreadInfo;");
            Check("NATIVE-TAPERED-INFO-USES-DISTINCT-COM-INTERFACE",
                taperedComInterfaceIsHandledIndependently, ref pass, ref fail,
                "TaperedThreadInfo is kept as its own Inventor COM interface; shared readers accept the object without an invalid ThreadInfo conversion.");

            bool taperedThreadFeatureAddUsesLateBoundComDispatch =
                nativeTaperedBuilderInterfaceSource.Contains("private static ThreadFeature AddTaperedThreadFeature(") &&
                nativeTaperedBuilderInterfaceSource.Contains("dynamic lateBoundThreadFeatures = threadFeatures;") &&
                nativeTaperedBuilderInterfaceSource.Contains("lateBoundThreadFeatures.Add(coneFace, startEdge, (object)taperedInfo,") &&
                nativeTaperedBuilderInterfaceSource.Contains("ThreadFeature feature = rawFeature as ThreadFeature;") &&
                nativeTaperedBuilderInterfaceSource.Contains("AddTaperedThreadFeature(threadFeatures, host.ConeFace, host.StartEdge, info,") &&
                !nativeTaperedBuilderInterfaceSource.Contains("threadFeatures.Add(host.ConeFace, host.StartEdge, info,");
            Check("NATIVE-TAPERED-THREADFEATURE-ADD-USES-LATE-BOUND-COM-DISPATCH",
                taperedThreadFeatureAddUsesLateBoundComDispatch, ref pass, ref fail,
                "ThreadFeatures.Add receives the native TaperedThreadInfo through COM dispatch without an invalid CLR ThreadInfo conversion and validates the returned ThreadFeature.");

            int taperLengthMethodStart = sourceRoot == null ? -1 : File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"))
                .IndexOf("private bool TryGetTaperStandardLengthMm(", StringComparison.Ordinal);
            string threadDialogOptionalLengthSource = File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"));
            int selectionChangedMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private void OnSelectionChanged()", StringComparison.Ordinal);
            int selectionChangedMethodEnd = selectionChangedMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void AutoSelectSize()", selectionChangedMethodStart, StringComparison.Ordinal);
            string selectionChangedMethodSource = selectionChangedMethodStart >= 0 && selectionChangedMethodEnd > selectionChangedMethodStart
                ? threadDialogOptionalLengthSource.Substring(selectionChangedMethodStart, selectionChangedMethodEnd - selectionChangedMethodStart)
                : string.Empty;
            int autoSelectSizeMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private void AutoSelectSize()", StringComparison.Ordinal);
            int autoSelectSizeMethodEnd = autoSelectSizeMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private string BuildSizeName(", autoSelectSizeMethodStart, StringComparison.Ordinal);
            string autoSelectSizeMethodSource = autoSelectSizeMethodStart >= 0 && autoSelectSizeMethodEnd > autoSelectSizeMethodStart
                ? threadDialogOptionalLengthSource.Substring(autoSelectSizeMethodStart, autoSelectSizeMethodEnd - autoSelectSizeMethodStart)
                : string.Empty;
            bool cylindricalAutoSelectionIsExcludedFromTaperedMode =
                selectionChangedMethodSource.Contains("if (chTapered == null || !chTapered.Checked)") &&
                selectionChangedMethodSource.Contains("AutoSelectSize();") &&
                autoSelectSizeMethodSource.Contains("if (chTapered != null && chTapered.Checked) return;") &&
                autoSelectSizeMethodSource.IndexOf("if (chTapered != null && chTapered.Checked) return;", StringComparison.Ordinal) <
                    autoSelectSizeMethodSource.IndexOf("new ThemedConfirmDialog(", StringComparison.Ordinal) &&
                selectionChangedMethodSource.Contains("RefreshTaperDesignations(true);");
            Check("TAPER-MODE-SUPPRESSES-ORDINARY-CYLINDRICAL-AUTO-SIZE-CONFIRMATION",
                cylindricalAutoSelectionIsExcludedFromTaperedMode, ref pass, ref fail,
                "A tapered edge skips cylindrical catalogue suggestion at selection change and AutoSelectSize is independently guarded; the NPT/JIS designation resolver remains active.");

            int taperLengthMethodEnd = taperLengthMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void MarkTaperLengthAsCustom(", taperLengthMethodStart, StringComparison.Ordinal);
            string taperLengthMethodSource = taperLengthMethodStart >= 0 && taperLengthMethodEnd > taperLengthMethodStart
                ? threadDialogOptionalLengthSource.Substring(taperLengthMethodStart, taperLengthMethodEnd - taperLengthMethodStart)
                : string.Empty;
            bool taperLengthFallbackIsNarrow =
                taperLengthMethodSource.Contains("catch (Exception ex) when (OptionalComPropertyPolicy.IsMemberUnavailable(ex))") &&
                !taperLengthMethodSource.Contains("catch { }") &&
                taperLengthMethodSource.Contains("Convert.ToDouble(rawValue, Inv)");
            Check("TAPER-LENGTH-THREADINFO-FALLBACK-SKIPS-ONLY-ABSENT-MEMBERS",
                taperLengthFallbackIsNarrow, ref pass, ref fail,
                "Length fallback advances only for a missing optional IDispatch member; other read/conversion errors reach the outer typed UI boundary.");

            int taperRefreshMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private void RefreshTaperDesignations(bool autoSelectForTarget = true)", StringComparison.Ordinal);
            int taperRefreshMethodEnd = taperRefreshMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void ApplyTaperCandidateWithConfirmation(", taperRefreshMethodStart, StringComparison.Ordinal);
            string taperRefreshMethodSource = taperRefreshMethodStart >= 0 && taperRefreshMethodEnd > taperRefreshMethodStart
                ? threadDialogOptionalLengthSource.Substring(taperRefreshMethodStart, taperRefreshMethodEnd - taperRefreshMethodStart)
                : string.Empty;
            int taperApplyMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private void ApplyTaperCandidateWithConfirmation(", StringComparison.Ordinal);
            int taperApplyMethodEnd = taperApplyMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void FlushPendingTaperRefreshAfterConfirmation(", taperApplyMethodStart, StringComparison.Ordinal);
            string taperApplyMethodSource = taperApplyMethodStart >= 0 && taperApplyMethodEnd > taperApplyMethodStart
                ? threadDialogOptionalLengthSource.Substring(taperApplyMethodStart, taperApplyMethodEnd - taperApplyMethodStart)
                : string.Empty;
            int taperFlushMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private void FlushPendingTaperRefreshAfterConfirmation(", StringComparison.Ordinal);
            int taperFlushMethodEnd = taperFlushMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void ApplyTaperTableLengthFromTable(", taperFlushMethodStart, StringComparison.Ordinal);
            string taperFlushMethodSource = taperFlushMethodStart >= 0 && taperFlushMethodEnd > taperFlushMethodStart
                ? threadDialogOptionalLengthSource.Substring(taperFlushMethodStart, taperFlushMethodEnd - taperFlushMethodStart)
                : string.Empty;
            int taperRestoreMethodStart = threadDialogOptionalLengthSource.IndexOf(
                "private bool TryRestoreTaperConfirmationResolution(", StringComparison.Ordinal);
            int taperRestoreMethodEnd = taperRestoreMethodStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "private void ScheduleTaperDesignationRefresh(", taperRestoreMethodStart, StringComparison.Ordinal);
            string taperRestoreMethodSource = taperRestoreMethodStart >= 0 && taperRestoreMethodEnd > taperRestoreMethodStart
                ? threadDialogOptionalLengthSource.Substring(taperRestoreMethodStart, taperRestoreMethodEnd - taperRestoreMethodStart)
                : string.Empty;

            bool taperRefreshDefersWhileConfirmationIsOpen =
                taperRefreshMethodSource.Contains("if (_taperConfirmationInProgress)") &&
                taperRefreshMethodSource.Contains("DeferTaperRefreshAfterConfirmation(") &&
                taperRefreshMethodSource.IndexOf("if (_taperConfirmationInProgress)", StringComparison.Ordinal) <
                    taperRefreshMethodSource.IndexOf("CancelTaperAutoSelection();", StringComparison.Ordinal);
            Check("TAPER-DESIGNATION-REFRESH-IS-DEFERRED-DURING-MODAL-CONFIRMATION",
                taperRefreshDefersWhileConfirmationIsOpen, ref pass, ref fail,
                "A nested WinForms message pump cannot rebuild/clear the designation ComboBox while its standard-size confirmation is active; the pending refresh records its selection context.");

            bool taperConfirmationIsReentrancyGuarded =
                taperApplyMethodSource.Contains("if (_taperConfirmationInProgress)") &&
                taperApplyMethodSource.Contains("_taperConfirmationInProgress = true;") &&
                taperApplyMethodSource.Contains("finally") &&
                taperApplyMethodSource.Contains("_taperConfirmationInProgress = false;") &&
                taperApplyMethodSource.Contains("taper-confirmation-duplicate-suppressed");
            Check("TAPER-STANDARD-SIZE-CONFIRMATION-SUPPRESSES-NESTED-PROMPTS",
                taperConfirmationIsReentrancyGuarded, ref pass, ref fail,
                "The modal confirmation is protected against duplicate nested calls and always releases its in-progress guard on close.");

            bool taperConfirmationDecisionIsResolvedOncePerSelectionContext =
                taperApplyMethodSource.Contains("TryRestoreTaperConfirmationResolution(") &&
                taperApplyMethodSource.Contains("RememberTaperConfirmationResolution(selectionKey, candidate, promptResult)") &&
                taperRestoreMethodSource.Contains("selectionKey, _taperConfirmationResolvedSelectionKey") &&
                taperRestoreMethodSource.Contains("DialogResult.Cancel") &&
                taperApplyMethodSource.Contains("taper-confirmation-opened") &&
                !taperApplyMethodSource.Contains("_acceptedTaperSizeSuggestionKey") &&
                threadDialogOptionalLengthSource.Contains("ClearTaperConfirmationResolution();") &&
                threadDialogOptionalLengthSource.Contains("if (!_loading && rbExt.Checked)") &&
                threadDialogOptionalLengthSource.Contains("if (!_loading && rbInt.Checked)");
            Check("TAPER-CONFIRMATION-DECISION-IS-REMEMBERED-ONCE-PER-SELECTION-CONTEXT",
                taperConfirmationDecisionIsResolvedOncePerSelectionContext, ref pass, ref fail,
                "Yes/No/Exit is remembered against the complete target/standard/diameter context; repeated callbacks cannot open another prompt or transfer an answer to a different designation.");

            bool taperDuplicatePendingRefreshIsDroppedForSameContext =
                taperFlushMethodSource.Contains("string.Equals(completedSelectionKey, currentSelectionKey, StringComparison.Ordinal)") &&
                taperFlushMethodSource.Contains("taper-duplicate-refresh-dropped-after-confirmation") &&
                taperFlushMethodSource.IndexOf("if (string.Equals(completedSelectionKey, currentSelectionKey", StringComparison.Ordinal) <
                    taperFlushMethodSource.IndexOf("ScheduleTaperDesignationRefresh(true);", StringComparison.Ordinal) &&
                taperFlushMethodSource.Contains("taper-context-refresh-flushed-after-confirmation");
            Check("TAPER-DUPLICATE-REFRESH-IS-DROPPED-AFTER-SAME-CONTEXT-CONFIRMATION",
                taperDuplicatePendingRefreshIsDroppedForSameContext, ref pass, ref fail,
                "A refresh queued by the modal nested message pump is discarded when the user is still on the same selection; a genuinely changed context is refreshed once.");

            bool taperCancelDismissalIsRememberedForTheCurrentContext =
                taperApplyMethodSource.Contains("RememberTaperConfirmationResolution(selectionKey, candidate, promptResult)") &&
                taperApplyMethodSource.Contains("if (promptResult == DialogResult.Cancel)") &&
                taperApplyMethodSource.Contains("FlushPendingTaperRefreshAfterConfirmation(selectionKey)") &&
                taperRestoreMethodSource.Contains("if (result == DialogResult.Cancel)") &&
                taperRestoreMethodSource.Contains("taper-confirmation-dismissed-duplicate-suppressed");
            Check("TAPER-EXIT-DISMISSES-REPEATED-PROMPTS-FOR-SAME-CONTEXT",
                taperCancelDismissalIsRememberedForTheCurrentContext, ref pass, ref fail,
                "Exit dismisses the current suggestion and suppresses duplicate queued prompts; a new selection/standard clears the remembered dismissal.");

            int taperDesignationHandlerStart = threadDialogOptionalLengthSource.IndexOf(
                "cbTaperDesignation.SelectedIndexChanged += (s, e) =>", StringComparison.Ordinal);
            int taperDesignationHandlerEnd = taperDesignationHandlerStart < 0 ? -1 : threadDialogOptionalLengthSource.IndexOf(
                "rbExt.CheckedChanged += (s, e) =>", taperDesignationHandlerStart, StringComparison.Ordinal);
            string taperDesignationHandlerSource = taperDesignationHandlerStart >= 0 && taperDesignationHandlerEnd > taperDesignationHandlerStart
                ? threadDialogOptionalLengthSource.Substring(taperDesignationHandlerStart, taperDesignationHandlerEnd - taperDesignationHandlerStart)
                : string.Empty;
            bool manualTaperDesignationClearsResolvedConfirmation =
                taperDesignationHandlerSource.Contains("if (!_refreshingTaperDesignations)") &&
                taperDesignationHandlerSource.Contains("ClearTaperConfirmationResolution();");
            Check("TAPER-MANUAL-DESIGNATION-CHANGE-CLEARS-REMEMBERED-AUTO-CONFIRMATION",
                manualTaperDesignationClearsResolvedConfirmation, ref pass, ref fail,
                "A deliberate manual designation change invalidates the remembered Yes/No/Exit decision; automatic ComboBox updates remain guarded by _refreshingTaperDesignations.");

            string[] iteration11ResourceKeys = {
                "ThreadDialog.NativeConversion.PrepareFailed", "ThreadDialog.NativeConversion.DownstreamCheckFailed",
                "NativeThreadConversionService.Error.ThreadDepthReadFailed", "NativeThreadConversionService.Error.ThreadOffsetReadFailed",
                "NativeThreadConversionService.Error.ThreadDepthValueReadFailed", "NativeThreadConversionService.Error.ThreadOffsetValueReadFailed",
                "NativeThreadConversionService.Error.NativeParametersReadFailed",
                "NativeThreadConversionService.Error.StartEdgeResolutionFailed",
                "NativeTaperedThreadBuilder.Error.PitchDepthReadFailed", "NativeTaperedThreadBuilder.Error.StandardDiameterReadFailed"
            };
            bool iteration11ResourcesValid = russianCatalog != null && englishCatalog != null &&
                iteration11ResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                (int)HelivantaErrorCode.TaperedNativePitchDepthReadFailed == 14 &&
                (int)HelivantaErrorCode.TaperedStandardDiameterReadFailed == 15 &&
                russianCatalog.GetString("PackagingThreadDialog.Preview.Status.Closed", CultureInfo.InvariantCulture) == null &&
                englishCatalog.GetString("PackagingThreadDialog.Preview.Status.Closed", CultureInfo.InvariantCulture) == null;
            Check("ITERATION11-NATIVE-CONVERSION-ERRORS-HAVE-STABLE-RU-EN-RESOURCES",
                iteration11ResourcesValid, ref pass, ref fail,
                "Native conversion and tapered table read failures use complete RU/EN resources, append-only stable codes, and the obsolete preview-closed status is removed.");

            string[] ribbonCommandResourceKeys = {
                "Ribbon.Command.CreateThread",
                "Ribbon.Command.EditThread"
            };
            bool ribbonCommandResourcesValid = russianCatalog != null && englishCatalog != null &&
                ribbonCommandResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                (russianCatalog.GetString("Ribbon.Command.CreateThread", CultureInfo.InvariantCulture) == "Создать резьбу") &&
                (russianCatalog.GetString("Ribbon.Command.EditThread", CultureInfo.InvariantCulture) == "Изменить резьбу") &&
                (englishCatalog.GetString("Ribbon.Command.CreateThread", CultureInfo.InvariantCulture) == "Create Thread") &&
                (englishCatalog.GetString("Ribbon.Command.EditThread", CultureInfo.InvariantCulture) == "Edit Thread");
            Check("ITERATION12-FIX7-FIX14-RIBBON-COMMAND-CAPTIONS-USE-SEMANTIC-RU-EN-RESOURCES",
                ribbonCommandResourcesValid, ref pass, ref fail,
                "Ribbon command identity/caption refresh compares semantic RU/EN resource values instead of depending on legacy dictionary translation.");

            string nativeConversionSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeThreadConversionService.cs"), Encoding.UTF8)
                : string.Empty;
            bool nativeRollbackRestoresHistory = nativeConversionSource.Contains("Rollback.EndOfPartRestore") &&
                nativeConversionSource.Contains("RestoreEndOfPartPosition(doc, originalEndOfPartSnapshot)") &&
                nativeConversionSource.Contains("bool nativeRestored = abortSucceeded && endOfPartRestored && modelSnapshotVerified &&") &&
                nativeConversionSource.Contains("ThreadBuilder.CaptureBuildRollbackSnapshotOrThrow(doc, \"NativeThreadConversionService.TryConvert\")") &&
                nativeConversionSource.Contains("ThreadBuilder.VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,");
            Check("ITERATION12-FIX7-FIX16-NATIVE-CONVERSION-ROLLBACK-RESTORES-AND-VERIFIES-HISTORY-MARKER",
                nativeRollbackRestoresHistory, ref pass, ref fail,
                "A native conversion rollback is not reported confirmed unless the captured End-of-Part position updates successfully and the unique original ThreadFeature snapshot is present.");
            Check("NATIVE-CONVERSION-ROLLBACK-VERIFIES-COMPLETE-ORIGINAL-MODEL-SNAPSHOT",
                nativeConversionSource.Contains("CaptureBuildRollbackSnapshotOrThrow(doc, \"NativeThreadConversionService.TryConvert\")") &&
                nativeConversionSource.Contains("VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,") &&
                nativeConversionSource.Contains("modelSnapshotVerified &&") &&
                nativeConversionSource.Contains("SourceFeatureExists(doc, plan)"),
                ref pass, ref fail,
                "A failed native ThreadFeature conversion must restore and verify the complete original B-rep, mass properties and all Helivanta metadata groups, as well as the unique original native feature and End-of-Part position.");

            string nativeTaperedBuilderForConversion = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeTaperedThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            string threadDialogForNativeConversion = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            bool nativeTaperedConversionMapsTableSpecification =
                nativeConversionSource.Contains("TaperedThreadInfo taperedInfo = rawInfo as TaperedThreadInfo;") &&
                nativeConversionSource.Contains("if (standardInfo == null && taperedInfo == null)") &&
                nativeConversionSource.Contains("TryCreateNativeConversionParameters(app, taperedInfo,") &&
                nativeTaperedBuilderForConversion.Contains("internal static bool TryCreateNativeConversionParameters(") &&
                nativeTaperedBuilderForConversion.Contains("TaperThreadTableAliasPolicy.IsExactNptType(nativeType)") &&
                nativeTaperedBuilderForConversion.Contains("TaperThreadTableAliasPolicy.IsCompatibleJisTaperType(nativeType, external)") &&
                nativeTaperedBuilderForConversion.Contains("TryGetNativePitchAndDepthMm(nativeInfo") &&
                nativeTaperedBuilderForConversion.Contains("TryGetNativePitchAndDepthMm(tableTaperedInfo") &&
                nativeTaperedBuilderForConversion.Contains("TryResolveTaperRadii(tableTaperedInfo, external, TaperGeometryModeKind.StrictStandard,");
            Check("NATIVE-TAPERED-THREADINFO-IS-MAPPED-TO-EXACT-INVENTOR-TABLE-ROW",
                nativeTaperedConversionMapsTableSpecification, ref pass, ref fail,
                "Native NPT/JIS TaperedThreadInfo is accepted separately from StandardThreadInfo, mapped by family and exact designation, then verified against the installed Thread.xls pitch and profile height.");

            bool nativeTaperedConversionUsesCylinderStockPreparation =
                nativeTaperedBuilderForConversion.Contains("source.IsConical || source.SelectedFace == null") &&
                nativeTaperedBuilderForConversion.Contains("source.SelectedFace.SurfaceType != SurfaceTypeEnum.kCylinderSurface") &&
                nativeTaperedBuilderForConversion.Contains("PreparedHost host = CreateConicalHost(app, doc, source, p, hostLengthCm,") &&
                nativeTaperedBuilderForConversion.Contains("CreatePhysicalThreadGroove(app, doc, source, p, host,") &&
                threadDialogForNativeConversion.Contains("TryBuildGeometryChangeSummary(") &&
                threadDialogForNativeConversion.Contains("parameters.TaperGeometryChangeConfirmed = true;");
            Check("NATIVE-NPT-CONVERSION-STARTS-FROM-CYLINDRICAL-STOCK-AND-PREPAIRS-TAPER-IN-HELIVANTA",
                nativeTaperedConversionUsesCylinderStockPreparation, ref pass, ref fail,
                "Conversion does not require a pre-existing cone: it validates cylindrical stock, prepares the 1:16 conical host from the selected native cosmetic thread table row, then builds the physical helical groove after explicit user confirmation.");

            bool nativeTaperedConversionLocksNativeSpecification =
                nativeConversionSource.Contains("parameters.RightHand != plan.RightHanded") &&
                nativeConversionSource.Contains("parameters.TaperGeometryMode != TaperGeometryModeKind.StrictStandard") &&
                nativeConversionSource.Contains("NearlyEqual(parameters.PitchMm, plan.Parameters.PitchMm, 1e-5)") &&
                threadDialogForNativeConversion.Contains("if (cbPitch != null) cbPitch.Enabled = false;") &&
                threadDialogForNativeConversion.Contains("plan.Parameters.PitchMm.ToString(\"0.#####\", Inv)") &&
                threadDialogForNativeConversion.Contains("if (cbTaperDesignation != null) cbTaperDesignation.Enabled = false;") &&
                threadDialogForNativeConversion.Contains("if (nStarts != null) nStarts.Enabled = false;");
            Check("NATIVE-NPT-CONVERSION-LOCKS-TABLE-IDENTITY-PITCH-HANDEDNESS",
                nativeTaperedConversionLocksNativeSpecification, ref pass, ref fail,
                "Native tapered conversion locks source designation, table pitch, handedness, single-start and strict table geometry at both UI and service boundaries; only physical length/offset remain adjustable.");

            string threadEditRollbackSourceForSnapshots = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8)
                : string.Empty;
            Check("STANDARD-THREAD-EDIT-ROLLBACK-VERIFIES-COMPLETE-ORIGINAL-MODEL-SNAPSHOT",
                threadEditRollbackSourceForSnapshots.Contains("CaptureBuildRollbackSnapshotOrThrow(doc, \"ThreadEditService.StandardEdit\")") &&
                threadEditRollbackSourceForSnapshots.Contains("VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,") &&
                threadEditRollbackSourceForSnapshots.Contains("originalEndOfPartRestored && modelSnapshotVerified &&") &&
                threadEditRollbackSourceForSnapshots.Contains("ThreadEditSafetyPolicy.IsRollbackVerified("),
                ref pass, ref fail,
                "Standard thread editing only confirms rollback after End-of-Part restoration, the full pre/post geometry-and-all-metadata fingerprint, and exact original group record/member checks.");

            Check("CAP-NECK-THREAD-EDIT-ROLLBACK-VERIFIES-COMPLETE-ORIGINAL-MODEL-SNAPSHOT",
                threadEditRollbackSourceForSnapshots.Contains("CaptureBuildRollbackSnapshotOrThrow(doc, \"ThreadEditService.CapNeckEdit\")") &&
                threadEditRollbackSourceForSnapshots.Contains("VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,") &&
                threadEditRollbackSourceForSnapshots.Contains("eopRestored && modelSnapshotVerified && found") &&
                threadEditRollbackSourceForSnapshots.Contains("count == originalCount"),
                ref pass, ref fail,
                "Cap/neck replacement rollback verifies the original B-rep, mass properties, all Helivanta metadata groups, End-of-Part and the exact original cap/neck group.");

            var buildRollbackOriginal = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.678, 123.456, 1.0, 2.0, 3.0);
            var buildRollbackEquivalent = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.67800004, 123.45600005, 1.00000004, 2.0, 3.0);
            var buildRollbackChangedFeatureCount = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 3, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.678, 123.456, 1.0, 2.0, 3.0);
            var buildRollbackChangedVolume = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.678, 123.46, 1.0, 2.0, 3.0);
            var buildRollbackChangedBrep = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 7, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.678, 123.456, 1.0, 2.0, 3.0);
            var buildRollbackChangedCenter = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.678, 123.456, 1.001, 2.0, 3.0);
            var buildRollbackChangedArea = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('A', 64), 45.70, 123.456, 1.0, 2.0, 3.0);
            var buildRollbackChangedMetadataCount = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 3, 15, new string('A', 64), 45.678, 123.456, 1.0, 2.0, 3.0);
            var buildRollbackChangedMetadataRecord = new ThreadBuildRollbackSnapshot(
                "part-internal-001", 1, 6, 12, 4, 3, 2, 1, 2, 0, 1, 2, 14, new string('B', 64), 45.678, 123.456, 1.0, 2.0, 3.0);
            bool buildRollbackPolicyAcceptsOnlyProvenState =
                ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackEquivalent) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(false, true, true, buildRollbackOriginal, buildRollbackEquivalent) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, false, true, buildRollbackOriginal, buildRollbackEquivalent) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, false, buildRollbackOriginal, buildRollbackEquivalent) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedFeatureCount) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedVolume) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedBrep) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedCenter) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedArea) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedMetadataCount) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, buildRollbackChangedMetadataRecord) &&
                !ThreadBuildRollbackPolicy.IsRollbackVerified(true, true, true, buildRollbackOriginal, null);
            Check("THREAD-BUILD-ROLLBACK-POLICY-REQUIRES-ABORT-UPDATE-DOCUMENT-AND-MODEL-FINGERPRINT",
                buildRollbackPolicyAcceptsOnlyProvenState, ref pass, ref fail,
                "Rollback verification requires successful Abort and Update2, the same active document, exact B-rep/work-feature/feature counts, unchanged complete metadata-group/member fingerprint, plus surface area, volume and center-of-mass within strict numeric tolerances.");

            string threadBuilderRollbackSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int buildAllSnapshotIndex = threadBuilderRollbackSource.IndexOf(
                "CaptureBuildRollbackSnapshotOrThrow(buildDocument, \"ThreadBuilder.BuildAll\")", StringComparison.Ordinal);
            int buildAllTransactionIndex = buildAllSnapshotIndex < 0 ? -1 : threadBuilderRollbackSource.IndexOf(
                "StartTransaction(", buildAllSnapshotIndex, StringComparison.Ordinal);
            int standardSnapshotIndex = threadBuilderRollbackSource.IndexOf(
                "CaptureBuildRollbackSnapshotOrThrow(doc, \"ThreadBuilder.Build\")", StringComparison.Ordinal);
            int standardTransactionIndex = standardSnapshotIndex < 0 ? -1 : threadBuilderRollbackSource.IndexOf(
                "StartTransaction(", standardSnapshotIndex, StringComparison.Ordinal);
            int taperedSnapshotIndex = threadBuilderRollbackSource.IndexOf(
                "CaptureBuildRollbackSnapshotOrThrow(doc, \"ThreadBuilder.Build.Tapered\")", StringComparison.Ordinal);
            int taperedTransactionIndex = taperedSnapshotIndex < 0 ? -1 : threadBuilderRollbackSource.IndexOf(
                "StartTransaction(", taperedSnapshotIndex, StringComparison.Ordinal);
            bool threadBuilderRollbackIsWiredForOwnedTransactions =
                buildAllSnapshotIndex >= 0 && buildAllTransactionIndex > buildAllSnapshotIndex &&
                standardSnapshotIndex >= 0 && standardTransactionIndex > standardSnapshotIndex &&
                taperedSnapshotIndex >= 0 && taperedTransactionIndex > taperedSnapshotIndex &&
                threadBuilderRollbackSource.Contains("VerifyBuildRollbackAfterAbort(app, buildDocument, rollbackSnapshot, abortSucceeded") &&
                threadBuilderRollbackSource.Contains("VerifyBuildRollbackAfterAbort(app, doc, rollbackSnapshot, abortSucceeded") &&
                threadBuilderRollbackSource.Contains("ThreadBuildRollbackPolicy.IsRollbackVerified(") &&
                threadBuilderRollbackSource.Contains("body.Faces.Count") && threadBuilderRollbackSource.Contains("body.Edges.Count") &&
                threadBuilderRollbackSource.Contains("massProperties.Area") && threadBuilderRollbackSource.Contains("massProperties.CenterOfMass") &&
                threadBuilderRollbackSource.Contains("massProperties.CacheResultsOnCompute = false") &&
                threadBuilderRollbackSource.Contains("massProperties.CacheResultsOnCompute = originalCacheResultsOnCompute") &&
                !threadBuilderRollbackSource.Contains("catch { try { nativeTransaction.Abort(); } catch { } throw; }");
            Check("THREAD-BUILDER-OWNED-TRANSACTIONS-VERIFY-ROLLBACK-AFTER-ABORT",
                threadBuilderRollbackIsWiredForOwnedTransactions, ref pass, ref fail,
                "BuildAll, standalone Build, and owned tapered Build capture a pre-transaction fingerprint without caching mass properties into the document, then verify after Abort; externally owned edit/conversion transactions keep their existing stronger verifiers.");

            Check("STANDARD-THREAD-BUILD-USES-BOUNDED-ABORT-RETRY",
                threadBuilderSourceForTransactions.Contains("TryAbortBuildTransactionWithRetries(tr, app, doc,") &&
                threadBuilderSourceForTransactions.Contains("\"ThreadBuilder.Build\", out abortException)") &&
                threadBuilderSourceForTransactions.Contains("VerifyBuildRollbackAfterAbort(app, doc, rollbackSnapshot, abortSucceeded"),
                ref pass, ref fail,
                "Standalone non-tapered thread builds must use the shared three-attempt Abort policy and verify the original geometry and metadata snapshot before reporting rollback.");
            Check("TAPERED-THREAD-BUILD-USES-BOUNDED-ABORT-RETRY",
                threadBuilderSourceForTransactions.Contains("TryAbortBuildTransactionWithRetries(nativeTransaction, app, doc,") &&
                threadBuilderSourceForTransactions.Contains("\"ThreadBuilder.Build.Tapered\", out abortException)") &&
                threadBuilderSourceForTransactions.Contains("VerifyBuildRollbackAfterAbort(app, doc, taperedRollbackSnapshot, abortSucceeded"),
                ref pass, ref fail,
                "Owned tapered thread builds must retain the native transaction reference for bounded Abort retries and verify the full pre-build snapshot.");
            int standaloneTransactionStart = threadBuilderSourceForTransactions.IndexOf(
                "tr = app.TransactionManager.StartTransaction(app.ActiveDocument, TransactionPrefix)", standardSnapshotIndex, StringComparison.Ordinal);
            int standaloneNullGuard = standaloneTransactionStart < 0 ? -1 : threadBuilderSourceForTransactions.IndexOf(
                "if (tr == null)", standaloneTransactionStart, StringComparison.Ordinal);
            int taperedTransactionStart = threadBuilderSourceForTransactions.IndexOf(
                "Transaction nativeTransaction = app.TransactionManager.StartTransaction", taperedSnapshotIndex, StringComparison.Ordinal);
            int taperedNullGuard = taperedTransactionStart < 0 ? -1 : threadBuilderSourceForTransactions.IndexOf(
                "if (nativeTransaction == null)", taperedTransactionStart, StringComparison.Ordinal);
            int taperedGeometryStart = taperedNullGuard < 0 ? -1 : threadBuilderSourceForTransactions.IndexOf(
                "NativeTaperedThreadBuilder.Build(app, t, p)", taperedNullGuard, StringComparison.Ordinal);
            Check("OWNED-THREAD-BUILD-REQUIRES-TRANSACTION-BEFORE-GEOMETRY",
                standaloneTransactionStart >= 0 && standaloneNullGuard > standaloneTransactionStart &&
                taperedTransactionStart >= 0 && taperedNullGuard > taperedTransactionStart &&
                taperedGeometryStart > taperedNullGuard,
                ref pass, ref fail,
                "Standalone cylindrical and owned tapered builds must stop before geometry creation when Inventor returns a null transaction.");

            string releaseRoot = !string.IsNullOrWhiteSpace(sourceRoot)
                ? Directory.GetParent(sourceRoot).FullName
                : string.Empty;
            string numberedReleaseProps = !string.IsNullOrWhiteSpace(releaseRoot)
                ? File.ReadAllText(Path.Combine(releaseRoot, "Directory.Build.props"), Encoding.UTF8)
                : string.Empty;
            string packageReleaseSource = !string.IsNullOrWhiteSpace(releaseRoot)
                ? File.ReadAllText(Path.Combine(releaseRoot, "tools", "package_release.py"), Encoding.UTF8)
                : string.Empty;
            string numberedReleaseAboutSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "AboutDialog.cs"), Encoding.UTF8)
                : string.Empty;
            string numberedReleaseSplashSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "LoadingSplash.cs"), Encoding.UTF8)
                : string.Empty;
            string numberedReleaseBuildSource = !string.IsNullOrWhiteSpace(releaseRoot)
                ? File.ReadAllText(Path.Combine(releaseRoot, "build.ps1"), Encoding.UTF8)
                : string.Empty;
            string numberedReleaseInstallerSource = !string.IsNullOrWhiteSpace(releaseRoot)
                ? File.ReadAllText(Path.Combine(releaseRoot, "installer", "make-installer.ps1"), Encoding.UTF8)
                : string.Empty;
            Match releaseVersionMatch = Regex.Match(numberedReleaseProps, @"<Version>\s*([^<\s]+)\s*</Version>");
            Match releaseAssemblyMatch = Regex.Match(numberedReleaseProps, @"<AssemblyVersion>\s*([^<\s]+)\s*</AssemblyVersion>");
            Match releaseFileMatch = Regex.Match(numberedReleaseProps, @"<FileVersion>\s*([^<\s]+)\s*</FileVersion>");
            Match releaseInfoMatch = Regex.Match(numberedReleaseProps, @"<InformationalVersion>\s*([^<\s]+)\s*</InformationalVersion>");
            string releaseVersion = releaseVersionMatch.Success ? releaseVersionMatch.Groups[1].Value : string.Empty;
            bool numberedReleaseIsConsistent = Regex.IsMatch(releaseVersion, @"^\d+\.\d+\.\d+$") &&
                releaseAssemblyMatch.Success && releaseAssemblyMatch.Groups[1].Value == releaseVersion + ".0" &&
                releaseFileMatch.Success && releaseFileMatch.Groups[1].Value == releaseVersion + ".0" &&
                releaseInfoMatch.Success && releaseInfoMatch.Groups[1].Value == releaseVersion &&
                string.Equals(Path.GetFileName(releaseRoot), "Helivanta-" + releaseVersion, StringComparison.Ordinal) &&
                packageReleaseSource.Contains("read_release_version(root)") &&
                packageReleaseSource.Contains("f\"Helivanta {release_version}\"") &&
                packageReleaseSource.Contains("f\"STATIC-VALIDATION-HELIVANTA-{release_version}.txt\"") &&
                packageReleaseSource.Contains("f\"Helivanta-{release_version}.zip\"") &&
                numberedReleaseBuildSource.Contains("Directory.Build.props") &&
                numberedReleaseBuildSource.Contains("$summary.Add(('Version: {0}' -f $displayVersion))") &&
                numberedReleaseInstallerSource.Contains("Directory.Build.props") &&
                numberedReleaseAboutSource.Contains("informational.InformationalVersion") &&
                numberedReleaseSplashSource.Contains("informational.InformationalVersion");
            Check("RELEASE-NUMBER-IS-CONSISTENT-ACROSS-PACKAGE-ASSEMBLY-UI-AND-ARCHIVE",
                numberedReleaseIsConsistent, ref pass, ref fail,
                "The release number comes from Directory.Build.props and agrees with assembly/file versions, generated package names, build summary, installer and user-visible version displays.");

            string browserMappingSource = string.Empty;
            if (!string.IsNullOrWhiteSpace(sourceRoot))
            {
                int browserMappingStart = threadBuilderRollbackSource.IndexOf(
                    "private static ObjectCollection CollectCreatedTopLevelBrowserNodes", StringComparison.Ordinal);
                int browserMappingEnd = threadBuilderRollbackSource.IndexOf(
                    "internal static bool SameBrowserComObject", StringComparison.Ordinal);
                if (browserMappingStart >= 0 && browserMappingEnd > browserMappingStart)
                    browserMappingSource = threadBuilderRollbackSource.Substring(browserMappingStart, browserMappingEnd - browserMappingStart);
            }
            Check("BROWSER-MAPPING-DOCUMENT-UPDATE-FAILURE-IS-DIAGNOSED-WITH-FALLBACK-PRESERVED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.DocumentUpdate") &&
                browserMappingSource.Contains("pane.GetBrowserNodeFromObject(obj)"),
                ref pass, ref fail, "A failed document update is retained in diagnostics while per-object BrowserNode resolution remains available.");
            Check("BROWSER-MAPPING-PANE-UPDATE-FAILURE-IS-DIAGNOSED-WITH-FALLBACK-PRESERVED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.BrowserPaneUpdate") &&
                browserMappingSource.Contains("GetBrowserNodeFromObject(obj)"),
                ref pass, ref fail, "A failed browser-pane refresh is recorded without removing the established per-object lookup fallback.");
            Check("BROWSER-MAPPING-PARTIAL-TREE-SNAPSHOT-FAILURE-IS-DIAGNOSED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.BrowserTreeSnapshot") &&
                browserMappingSource.Contains("browserOrder.Add(node)"),
                ref pass, ref fail, "A partial BrowserNodes enumeration remains a recoverable snapshot fallback but can no longer fail silently.");
            Check("BROWSER-MAPPING-OBJECT-LOOKUP-COM-FAILURE-IS-DIAGNOSED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.GetBrowserNodeFromObject") &&
                browserMappingSource.Contains("node = null;"),
                ref pass, ref fail, "Failed GetBrowserNodeFromObject COM calls are diagnosed while the existing missing-node/fail-closed result is preserved.");
            Check("BROWSER-MAPPING-NATIVE-OBJECT-COM-FAILURE-IS-DIAGNOSED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.NativeObject") &&
                browserMappingSource.Contains("node.NativeObject"),
                ref pass, ref fail, "A failed BrowserNode.NativeObject read is recorded while the per-created-object fallback remains available.");
            Check("BROWSER-MAPPING-PARENT-LOOKUP-COM-FAILURE-IS-DIAGNOSED-AND-REJECTED",
                browserMappingSource.Contains("ThreadBuilder.CollectCreatedTopLevelBrowserNodes.ParentLookup") &&
                browserMappingSource.Contains("parentLookupException") &&
                browserMappingSource.Contains("continue;"),
                ref pass, ref fail, "An unreadable parent is not treated as a valid top-level mapping: the COM error is logged and that node remains excluded.");

            string comObjectIdentitySource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ComObjectIdentity.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ComObjectIdentity.cs"), Encoding.UTF8)
                : string.Empty;
            Check("COM-IDENTITY-COMPARISON-DISTINGUISHES-DIFFERENT-FROM-UNAVAILABLE",
                comObjectIdentitySource.Contains("enum ComIdentityComparison") &&
                comObjectIdentitySource.Contains("Same,") &&
                comObjectIdentitySource.Contains("Different,") &&
                comObjectIdentitySource.Contains("Unavailable") &&
                comObjectIdentitySource.Contains("Marshal.IsComObject") &&
                comObjectIdentitySource.Contains("Marshal.GetIUnknownForObject") &&
                comObjectIdentitySource.Contains("Marshal.Release(pointer)"),
                ref pass, ref fail, "COM identity checks distinguish confirmed mismatch from unavailable identity and balance only the temporary IUnknown reference without releasing caller RCWs.");

            int browserTreeSnapshotStart = threadBuilderSourceForTransactions.IndexOf(
                "private static void CollectBrowserTreeOccurrences", StringComparison.Ordinal);
            int browserTreeSnapshotEnd = threadBuilderSourceForTransactions.IndexOf(
                "private static List<BrowserTreeOccurrence> SnapshotBrowserTree", browserTreeSnapshotStart < 0 ? 0 : browserTreeSnapshotStart, StringComparison.Ordinal);
            string browserTreeSnapshotSource = browserTreeSnapshotStart >= 0 && browserTreeSnapshotEnd > browserTreeSnapshotStart
                ? threadBuilderSourceForTransactions.Substring(browserTreeSnapshotStart, browserTreeSnapshotEnd - browserTreeSnapshotStart)
                : string.Empty;
            Check("TAPERED-FOLDER-SNAPSHOT-NATIVEOBJECT-READ-FAILURE-MARKS-SCAN-INCOMPLETE",
                browserTreeSnapshotSource.Contains("child.NativeObject") &&
                browserTreeSnapshotSource.Contains("truncated = true;") &&
                browserTreeSnapshotSource.Contains("ThreadBuilder.CollectBrowserTreeOccurrences.NativeObject"),
                ref pass, ref fail, "A failed NativeObject read marks the browser snapshot incomplete and cannot be interpreted as an object absent from the tree.");

            Check("TAPERED-FOLDER-SNAPSHOT-NULL-CHILD-MARKS-SCAN-INCOMPLETE",
                Regex.IsMatch(browserTreeSnapshotSource,
                    @"if\s*\(child\s*==\s*null\)\s*\{\s*//[\s\S]*?truncated\s*=\s*true\s*;[\s\S]*?folder-repair-tree-null-child[\s\S]*?return\s*;"),
                ref pass, ref fail, "A null BrowserNode entry invalidates the authoritative folder-repair snapshot and cannot be skipped as an absent occurrence.");

            bool folderOwnershipByNodeIdentity = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                true, ComIdentityComparison.Same, false, false, ComIdentityComparison.Unavailable,
                false, ComIdentityComparison.Unavailable, out bool folderOwnershipByNodeComplete);
            bool folderOwnershipByBrowserFolderNativeIdentity = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                false, ComIdentityComparison.Unavailable, true, true, ComIdentityComparison.Same,
                false, ComIdentityComparison.Unavailable, out bool folderOwnershipByBrowserFolderNativeComplete);
            bool folderOwnershipByFolderNodeNativeIdentity = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                true, ComIdentityComparison.Different, true, true, ComIdentityComparison.Different,
                true, ComIdentityComparison.Same, out bool folderOwnershipByFolderNodeNativeComplete);
            bool folderOwnershipNameOnly = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                false, ComIdentityComparison.Unavailable, false, false, ComIdentityComparison.Unavailable,
                false, ComIdentityComparison.Unavailable, out bool folderOwnershipNameOnlyComplete);
            bool folderOwnershipIdentityUnavailable = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                false, ComIdentityComparison.Unavailable, true, true, ComIdentityComparison.Different,
                false, ComIdentityComparison.Unavailable, out bool folderOwnershipUnavailableComplete);
            bool folderOwnershipNativeUnavailable = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                true, ComIdentityComparison.Different, false, true, ComIdentityComparison.Unavailable,
                false, ComIdentityComparison.Unavailable, out bool folderOwnershipNativeUnavailableComplete);
            bool folderOwnershipConfirmedDifferent = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                true, ComIdentityComparison.Different, true, true, ComIdentityComparison.Different,
                true, ComIdentityComparison.Different, out bool folderOwnershipDifferentComplete);
            bool folderOwnershipUnknownEnum = BrowserFolderOwnershipPolicy.IsConfirmedSame(
                true, (ComIdentityComparison)99, true, true, (ComIdentityComparison)99,
                true, (ComIdentityComparison)99, out bool folderOwnershipUnknownEnumComplete);
            bool knownStructuralNodeNonOwner = BrowserFolderOwnershipPolicy.IsConfirmedKnownStructuralNonOwner(
                true, ComIdentityComparison.Different, true, false, true,
                out bool knownStructuralNodeNonOwnerComplete);
            bool structuralNodeNativeReadUnavailable = BrowserFolderOwnershipPolicy.IsConfirmedKnownStructuralNonOwner(
                true, ComIdentityComparison.Different, false, false, true,
                out bool structuralNodeNativeReadUnavailableComplete);
            bool customNodeNullNativeNotStructural = BrowserFolderOwnershipPolicy.IsConfirmedKnownStructuralNonOwner(
                true, ComIdentityComparison.Different, true, false, false,
                out bool customNodeNullNativeNotStructuralComplete);
            bool structuralNodeIdentityUnavailable = BrowserFolderOwnershipPolicy.IsConfirmedKnownStructuralNonOwner(
                true, ComIdentityComparison.Unavailable, true, false, true,
                out bool structuralNodeIdentityUnavailableComplete);
            Check("BROWSER-FOLDER-OWNERSHIP-REQUIRES-POSITIVE-COM-IDENTITY-AND-FAILS-CLOSED",
                folderOwnershipByNodeIdentity && folderOwnershipByNodeComplete &&
                folderOwnershipByBrowserFolderNativeIdentity && folderOwnershipByBrowserFolderNativeComplete &&
                folderOwnershipByFolderNodeNativeIdentity && folderOwnershipByFolderNodeNativeComplete &&
                !folderOwnershipNameOnly && !folderOwnershipNameOnlyComplete &&
                !folderOwnershipIdentityUnavailable && !folderOwnershipUnavailableComplete &&
                !folderOwnershipNativeUnavailable && !folderOwnershipNativeUnavailableComplete &&
                !folderOwnershipConfirmedDifferent && folderOwnershipDifferentComplete &&
                !folderOwnershipUnknownEnum && !folderOwnershipUnknownEnumComplete &&
                knownStructuralNodeNonOwner && knownStructuralNodeNonOwnerComplete &&
                !structuralNodeNativeReadUnavailable && !structuralNodeNativeReadUnavailableComplete &&
                !customNodeNullNativeNotStructural && !customNodeNullNativeNotStructuralComplete &&
                !structuralNodeIdentityUnavailable && !structuralNodeIdentityUnavailableComplete,
                ref pass, ref fail, "Folder ownership accepts only positive COM identity; a verified built-in structural row with a successfully-read null NativeObject and a confirmed different BrowserNode identity is a non-owner. Name-only, custom, unreadable and unknown identity remain fail-closed.");

            Check("BROWSER-TREE-REPAIR-SNAPSHOT-POLICY-REJECTS-PARTIAL-OR-NULL-CHILD-SCANS",
                BrowserTreeRepairSnapshotPolicy.CanUseForOwnership(true, false) &&
                !BrowserTreeRepairSnapshotPolicy.CanUseForOwnership(false, false) &&
                !BrowserTreeRepairSnapshotPolicy.CanUseForOwnership(true, true) &&
                !BrowserTreeRepairSnapshotPolicy.CanUseForOwnership(false, true),
                ref pass, ref fail, "Authoritative BrowserFolder repair requires complete traversal and zero null child entries; diagnostic snapshots do not override this gate.");

            List<string> templateLabelsRu = ThreadStandardLocalizationPolicy.GetDisplayNames("ru");
            List<string> templateLabelsEn = ThreadStandardLocalizationPolicy.GetDisplayNames("en");
            HelivantaUserSettings.ReloadPersisted();
            string persistedTemplateLanguage = HelivantaUserSettings.Current == null ? "ru" :
                (string.Equals(HelivantaUserSettings.Current.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru");
            List<string> persistedTemplateLabels = ThreadStandardLocalizationPolicy.GetDisplayNames(persistedTemplateLanguage);
            ThreadStandard[] localizedTemplateStandards = Enum.GetValues(typeof(ThreadStandard))
                .Cast<ThreadStandard>()
                .Where(standard => standard != ThreadStandard.GostPipeTapered)
                .ToArray();
            bool resourceParity = localizedTemplateStandards.Length == 9 &&
                templateLabelsRu.Count == localizedTemplateStandards.Length &&
                templateLabelsEn.Count == localizedTemplateStandards.Length;
            for (int i = 0; i < localizedTemplateStandards.Length && resourceParity; i++)
            {
                string source = ThreadStandards.Title(localizedTemplateStandards[i]);
                resourceParity = HelivantaLegacyResourceMap.RussianToResourceId.TryGetValue(source, out string resourceKey) &&
                    string.Equals(templateLabelsRu[i], HelivantaLocalization.GetForLanguage(resourceKey, "ru"), StringComparison.Ordinal) &&
                    string.Equals(templateLabelsEn[i], HelivantaLocalization.GetForLanguage(resourceKey, "en"), StringComparison.Ordinal);
            }
            bool uniqueTemplateLabels = templateLabelsRu.Distinct(StringComparer.Ordinal).Count() == 9 &&
                templateLabelsEn.Distinct(StringComparer.Ordinal).Count() == 9;
            bool englishTemplateLabelsHaveNoCyrillic = templateLabelsEn.All(label =>
                !string.IsNullOrWhiteSpace(label) && !HelivantaLocalization.ContainsCyrillic(label));
            bool persistedTemplateLanguageCorrect = persistedTemplateLabels.Count == 9 &&
                persistedTemplateLabels.SequenceEqual(
                    persistedTemplateLanguage == "en" ? templateLabelsEn : templateLabelsRu,
                    StringComparer.Ordinal);
            Check("THREAD-TEMPLATE-COMBO-LOCALIZATION-USES-PERSISTED-SETTINGS-AFTER-RELOAD",
                resourceParity && uniqueTemplateLabels && englishTemplateLabelsHaveNoCyrillic && persistedTemplateLanguageCorrect,
                ref pass, ref fail,
                "All nine Template items must match their exact RU/EN resource mappings and the list selected from persisted Settings. Language-neutral labels such as ANSI may remain identical in both languages.");

            bool comboPopulationMatchesPersistedLanguage = false;
            using (var templateCombo = new ComboBox())
            {
                int requestedSelection = 4;
                int actualSelection = ThreadStandardLocalizationPolicy.PopulateComboBoxItems(
                    templateCombo, persistedTemplateLanguage, requestedSelection);
                string[] actualItems = templateCombo.Items.Cast<object>().Select(item => item == null ? null : item.ToString()).ToArray();
                comboPopulationMatchesPersistedLanguage = actualSelection == requestedSelection &&
                    templateCombo.SelectedIndex == requestedSelection &&
                    actualItems.SequenceEqual(persistedTemplateLabels, StringComparer.Ordinal);
            }
            Check("THREAD-TEMPLATE-COMBO-POPULATES-ACTUAL-WINFORMS-ITEMS-FROM-PERSISTED-LANGUAGE",
                comboPopulationMatchesPersistedLanguage,
                ref pass, ref fail,
                "The production ComboBox population helper must create the actual nine WinForms Items in the reloaded Settings language and preserve the requested selection index.");

            int browserFolderIdentityStart = threadBuilderSourceForTransactions.IndexOf(
                "private static bool BrowserNodeRepresentsFolder(", StringComparison.Ordinal);
            int browserFolderIdentityEnd = threadBuilderSourceForTransactions.IndexOf(
                "private static List<BrowserFolder> GetThreadFolderOwners(", browserFolderIdentityStart < 0 ? 0 : browserFolderIdentityStart, StringComparison.Ordinal);
            string browserFolderIdentitySource = browserFolderIdentityStart >= 0 && browserFolderIdentityEnd > browserFolderIdentityStart
                ? threadBuilderSourceForTransactions.Substring(browserFolderIdentityStart, browserFolderIdentityEnd - browserFolderIdentityStart)
                : string.Empty;
            Check("BROWSER-FOLDER-OWNERSHIP-SKIPS-ONLY-VERIFIED-BUILTIN-STRUCTURAL-NULL-NATIVE-ROWS",
                browserFolderIdentitySource.Contains("treeNativeReadComplete") &&
                browserFolderIdentitySource.Contains("IsKnownStructuralBrowserNodeWithoutNativeObject(treeNode)") &&
                browserFolderIdentitySource.Contains("IsConfirmedKnownStructuralNonOwner") &&
                threadBuilderSourceForTransactions.Contains("bool builtIn = definition.BuiltIn") &&
                threadBuilderSourceForTransactions.Contains("if (node.NativeObject != null)"),
                ref pass, ref fail,
                "The only accepted non-owner shortcut is a readable null NativeObject plus BuiltIn BrowserNodeDefinition and a confirmed different BrowserNode COM identity; arbitrary null/custom/unreadable nodes remain unresolved.");

            Check("BROWSER-FOLDER-OWNERSHIP-DOES-NOT-USE-NAME-OR-LABEL-AS-PROOF",
                browserFolderIdentitySource.Contains("BrowserFolderOwnershipPolicy.IsConfirmedSame") &&
                browserFolderIdentitySource.Contains("ComIdentityComparison.Same") &&
                browserFolderIdentitySource.Contains("ComIdentityComparison.Unavailable") &&
                browserFolderIdentitySource.Contains("TreeNativeObjectToBrowserFolder") &&
                browserFolderIdentitySource.Contains("folderNode.NativeObject") &&
                !browserFolderIdentitySource.Contains("string.Equals(nativeName, folderName") &&
                !browserFolderIdentitySource.Contains("folder.Name") &&
                !browserFolderIdentitySource.Contains("dynamicTreeNative.Name"),
                ref pass, ref fail, "A managed-folder prefix or matching display/native name cannot override confirmed COM identity mismatch or unavailable identity.");

            int taperedFolderScanStart = threadBuilderSourceForTransactions.IndexOf(
                "private static List<BrowserFolder> GetRootHelivantaThreadFolders", StringComparison.Ordinal);
            int taperedFolderScanEnd = threadBuilderSourceForTransactions.IndexOf(
                "private static string DescribeRootThreadFolderInventory", taperedFolderScanStart < 0 ? 0 : taperedFolderScanStart, StringComparison.Ordinal);
            string taperedFolderScanSource = taperedFolderScanStart >= 0 && taperedFolderScanEnd > taperedFolderScanStart
                ? threadBuilderSourceForTransactions.Substring(taperedFolderScanStart, taperedFolderScanEnd - taperedFolderScanStart)
                : string.Empty;
            Check("TAPERED-ROOT-FOLDER-INVENTORY-FAILS-CLOSED-ON-UNREADABLE-PROPERTIES-OR-IDENTITY",
                taperedFolderScanSource.Contains("TryIsHelivantaThreadFolder") &&
                taperedFolderScanSource.Contains("enumerationComplete = false;") &&
                taperedFolderScanSource.Contains("ComIdentityComparison.Unavailable") &&
                taperedFolderScanSource.Contains("BrowserFolder classification failed"),
                ref pass, ref fail, "Unreadable PartFeatureFolder/Name properties, null entries and unavailable COM identity prevent a partial root-folder inventory from being used for ownership decisions.");

            int folderRepairStart = threadBuilderSourceForTransactions.IndexOf(
                "private static bool TryRepairSharedExistingThreadFolder", StringComparison.Ordinal);
            int folderRepairEnd = threadBuilderSourceForTransactions.IndexOf(
                "private static bool ", folderRepairStart < 0 ? 0 : folderRepairStart + 40, StringComparison.Ordinal);
            string folderRepairSource = folderRepairStart >= 0 && folderRepairEnd > folderRepairStart
                ? threadBuilderSourceForTransactions.Substring(folderRepairStart, folderRepairEnd - folderRepairStart)
                : threadBuilderSourceForTransactions.Substring(folderRepairStart < 0 ? 0 : folderRepairStart);
            Check("TAPERED-FOLDER-REPAIR-ABORTS-ON-UNKNOWN-IDENTITY",
                folderRepairSource.Contains("occurrenceIdentityComplete") &&
                folderRepairSource.Contains("ownerIdentityComplete") &&
                folderRepairSource.Contains("membershipIdentityComplete") &&
                folderRepairSource.Contains("ComIdentityComparison.Unavailable") &&
                folderRepairSource.Contains("repairMustAbortOnFailure = true;"),
                ref pass, ref fail, "Folder reuse/move logic proceeds only with complete identity evidence; an unknown comparison does not become a confirmed non-match.");

            Check("CAPNECK-EDIT-REUSES-EXISTING-PACKAGING-PART-FEATURE-FOLDER",
                threadBuilderSourceForTransactions.Contains("packagingThreadFolder: true") &&
                threadBuilderSourceForTransactions.Contains("TryRepairSharedExistingThreadFolder(app, doc, pane, created, packagingThreadFolder") &&
                threadBuilderSourceForTransactions.Contains("GetRootHelivantaThreadFolders(topNode, packagingThread") &&
                threadBuilderSourceForTransactions.Contains("ThreadBrowserFolderPolicy.IsThreadFolderForKind(name, packagingThread)") &&
                threadBuilderSourceForTransactions.Contains("SnapshotBrowserTree(topNode, out truncated)"),
                ref pass, ref fail,
                "Cap/Neck replacement scans actual BrowserTree occurrences by COM identity and reuses its existing packaging PartFeatureFolder instead of relying on GetBrowserNodeFromObject's first logical occurrence.");
            string browserFolderRenamePolicySource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "BrowserFolderRenamePolicy.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "BrowserFolderRenamePolicy.cs"), Encoding.UTF8)
                : string.Empty;
            Check("CAPNECK-EDIT-RENAMES-REUSED-FOLDER-AND-VERIFIES-READBACK",
                threadBuilderSourceForTransactions.Contains("TryRenameExistingThreadFolder(topNode, candidate, requestedFolderName") &&
                threadBuilderSourceForTransactions.Contains("BrowserFolderRenamePolicy.TryApply(") &&
                browserFolderRenamePolicySource.Contains("writeName(requestedName);") &&
                browserFolderRenamePolicySource.Contains("string.Equals(readBack, requestedName, StringComparison.OrdinalIgnoreCase)") &&
                threadBuilderSourceForTransactions.Contains("TryRenameExistingThreadFolder.ExcludeCurrentFolder"),
                ref pass, ref fail,
                "A reused Cap/Neck folder is renamed only after collision checks excluding that folder itself; the setter is delegated to a readback-verified mutation policy.");
            Check("CAPNECK-EDIT-RENAME-MUTATION-FLAG-PRECEDES-COM-SETTER-AND-RESTORES-ON-ERROR",
                browserFolderRenamePolicySource.IndexOf("mutationAttempted = true;", StringComparison.Ordinal) >= 0 &&
                browserFolderRenamePolicySource.IndexOf("mutationAttempted = true;", StringComparison.Ordinal) <
                    browserFolderRenamePolicySource.IndexOf("writeName(requestedName);", StringComparison.Ordinal) &&
                browserFolderRenamePolicySource.Contains("TryRestoreOriginalName(originalName, readName, writeName") &&
                threadBuilderSourceForTransactions.Contains("out finalFolderName, out renameError, out renameMutationAttempted") &&
                threadBuilderSourceForTransactions.Contains("repairMutationAttempted = repairMutationAttempted || renameMutationAttempted;"),
                ref pass, ref fail,
                "The repair-mutation flag is set before the COM name setter and propagated on both success and failure; a setter that throws after mutation triggers verified restoration and remains transaction-fatal.");
            Check("CAPNECK-EDIT-AMBIGUOUS-REUSE-DOES-NOT-ATTEMPT-SECOND-GROUPING",
                threadBuilderSourceForTransactions.Contains("bestEffortExistingFolderRepairFailure") &&
                threadBuilderSourceForTransactions.Contains("repairMutationAttempted = true;") &&
                threadBuilderSourceForTransactions.Contains("no-new-folder-attempted=true") &&
                threadBuilderSourceForTransactions.IndexOf("if (bestEffortExistingFolderRepairFailure && !repairMutationAttempted)", StringComparison.Ordinal) <
                    threadBuilderSourceForTransactions.IndexOf("ObjectCollection browserNodes = CollectCreatedTopLevelBrowserNodes", StringComparison.Ordinal),
                ref pass, ref fail,
                "If an existing Cap/Neck folder is present but ownership/identity is ambiguous before a tree mutation, replacement keeps committed geometry and metadata and does not create a competing folder. Once Folder.Add is attempted, any failure remains transaction-fatal because Inventor may have partially moved nodes.");

            string browserDiagnosticsSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "BrowserTreeDiagnostics.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "BrowserTreeDiagnostics.cs"), Encoding.UTF8)
                : string.Empty;
            Check("BROWSER-TREE-DIAGNOSTICS-REPORTS-TOPNODE-AND-NATIVEOBJECT-READ-FAILURES",
                browserDiagnosticsSource.Contains("topNodeReadFailure") &&
                browserDiagnosticsSource.Contains("nativeReadFailure") &&
                browserDiagnosticsSource.Contains("identity-unavailable") &&
                browserDiagnosticsSource.Contains("not-com-object"),
                ref pass, ref fail, "The diagnostic snapshot distinguishes actual nulls from failed COM reads and unavailable COM identity.");
            Check("BROWSER-TREE-DIAGNOSTICS-EXPLICITLY-MARKS-INCOMPLETE-READ-ONLY-SNAPSHOTS",
                browserDiagnosticsSource.Contains("SNAPSHOT diagnosticOnly=true; captureComplete=") &&
                browserDiagnosticsSource.Contains("reason=max-depth") &&
                browserDiagnosticsSource.Contains("reason=max-nodes") &&
                browserDiagnosticsSource.Contains("enumerationIncomplete=true") &&
                browserDiagnosticsSource.Contains("children-unavailable=") &&
                browserDiagnosticsSource.Contains("PARENT truncated=true") &&
                browserDiagnosticsSource.Contains("ref bool captureComplete"),
                ref pass, ref fail,
                "BrowserTreeDiagnostics remains read-only and bounded, while null entries, COM read failures and traversal limits are explicit; captureComplete is diagnostic metadata only, never ownership evidence.");
            bool browserSnapshotCallersAreFireAndForget =
                threadBuilderSourceForTransactions.Contains("BrowserTreeDiagnostics.WriteSnapshot(") &&
                threadEditServiceSourceForPackaging.Contains("BrowserTreeDiagnostics.WriteSnapshot(") &&
                !Regex.IsMatch(threadBuilderSourceForTransactions, @"=\s*BrowserTreeDiagnostics\.WriteSnapshot\s*\(") &&
                !Regex.IsMatch(threadEditServiceSourceForPackaging, @"=\s*BrowserTreeDiagnostics\.WriteSnapshot\s*\(") &&
                browserDiagnosticsSource.Contains("internal static void WriteSnapshot(");
            Check("BROWSER-TREE-DIAGNOSTICS-SNAPSHOT-IS-NOT-AN-OWNERSHIP-OR-CONTROL-FLOW-RESULT",
                browserSnapshotCallersAreFireAndForget, ref pass, ref fail,
                "Tree snapshots are void diagnostic side effects; ThreadBuilder and ThreadEditService do not consume a snapshot result to prove ownership or decide model mutation.");

            int originalEdgeRecoveryStart = threadEditServiceSourceForPackaging.IndexOf(
                "internal static ThreadTarget ResolveOriginalTarget", StringComparison.Ordinal);
            int originalEdgeRecoveryEnd = threadEditServiceSourceForPackaging.IndexOf(
                "private static bool TryReadRecoveryEdgeCenter", originalEdgeRecoveryStart < 0 ? 0 : originalEdgeRecoveryStart, StringComparison.Ordinal);
            string originalEdgeRecoverySource = originalEdgeRecoveryStart >= 0 && originalEdgeRecoveryEnd > originalEdgeRecoveryStart
                ? threadEditServiceSourceForPackaging.Substring(originalEdgeRecoveryStart, originalEdgeRecoveryEnd - originalEdgeRecoveryStart)
                : string.Empty;
            Check("THREAD-EDIT-ORIGINAL-EDGE-RECOVERY-REQUIRES-COM-IDENTITY",
                originalEdgeRecoverySource.Contains("ComObjectIdentity.TryGetCanonicalIdentity(edge") &&
                originalEdgeRecoverySource.Contains("seenEdgeIdentities.Add(identity)") &&
                originalEdgeRecoverySource.Contains("return null;") &&
                !originalEdgeRecoverySource.Contains("SameGeometricEdge"),
                ref pass, ref fail, "A plausible original edge whose COM identity cannot be read aborts recovery instead of being geometrically deduplicated into a false unique candidate.");

            string rollbackWarningRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadBuilder.Error.RollbackUnverified", CultureInfo.InvariantCulture);
            string rollbackWarningEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadBuilder.Error.RollbackUnverified", CultureInfo.InvariantCulture);
            string snapshotWarningRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadBuilder.Error.RollbackSnapshotUnavailable", CultureInfo.InvariantCulture);
            string snapshotWarningEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadBuilder.Error.RollbackSnapshotUnavailable", CultureInfo.InvariantCulture);
            bool threadBuildRollbackResourcesValid = !string.IsNullOrWhiteSpace(rollbackWarningRu) &&
                rollbackWarningRu.Contains("НЕ СОХРАНЯЙТЕ") && !string.IsNullOrWhiteSpace(rollbackWarningEn) &&
                rollbackWarningEn.Contains("DO NOT SAVE") &&
                !rollbackWarningEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !string.IsNullOrWhiteSpace(snapshotWarningRu) && snapshotWarningRu.Contains("Построение не начато") &&
                !string.IsNullOrWhiteSpace(snapshotWarningEn) && snapshotWarningEn.Contains("build was not started") &&
                (int)HelivantaErrorCode.ThreadBuildRollbackUnverified == 35 &&
                (int)HelivantaErrorCode.ThreadBuildRollbackSnapshotUnavailable == 36 &&
                ThreadBuilderRollbackCodeMappingIsStable(threadBuilderRollbackSource);
            Check("THREAD-BUILD-ROLLBACK-HAS-APPENDED-CODES-AND-COMPLETE-RU-EN-RECOVERY-MESSAGES",
                threadBuildRollbackResourcesValid, ref pass, ref fail,
                "An unverified rollback shows a localized DO NOT SAVE instruction; failure to capture the baseline prevents the transaction from opening; error codes are appended 35/36.");
            bool nativeAbortFailureKeepsRecoveryPath =
                nativeConversionSource.Contains("abortSucceeded = TryAbortTransactionWithRetries(app, doc, transaction, stage);") &&
                nativeConversionSource.Contains("for (int attempt = 1; attempt <= 3; attempt++)") &&
                nativeConversionSource.Contains("bool nativeRestored = abortSucceeded && endOfPartRestored && modelSnapshotVerified &&") &&
                nativeConversionSource.Contains("if (transaction != null && !abortAttemptsCompleted)") &&
                nativeConversionSource.Contains("Transaction.Abort.no-reference");
            Check("ITERATION12-FIX7-FIX21-NATIVE-CONVERSION-ABORT-RETRIES-FEED-ROLLBACK-VERIFICATION",
                nativeAbortFailureKeepsRecoveryPath, ref pass, ref fail,
                "All bounded Abort attempts are completed before rollback status is calculated; a missing transaction reference fails closed, and an Abort success is still followed by End-of-Part and source-feature verification.");
            string threadEditRollbackSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8)
                : string.Empty;
            bool packagingEditRetriesAbortAndRetainsReference =
                threadEditRollbackSource.Contains("TryAbortTransactionWithRetries(transaction, app, doc,") &&
                threadEditRollbackSource.Contains("\"CapNeckEdit\", threadId, stage, out abortFailure)") &&
                threadEditRollbackSource.Contains("if (abortSucceeded) transaction = null;") &&
                threadEditRollbackSource.Contains("Все три попытки Abort завершились ошибкой") &&
                threadEditRollbackSource.Contains("for (int attempt = 1; attempt <= 3; attempt++)") &&
                threadEditRollbackSource.Contains("editKind=");
            Check("ITERATION12-FIX7-FIX21-CAP-NECK-EDIT-RETRIES-ABORT-BEFORE-RECOVERY-CHECK",
                packagingEditRetriesAbortAndRetainsReference, ref pass, ref fail,
                "Cap/neck editing performs up to three Abort attempts, retains the COM transaction reference after failed attempts, and only verifies original metadata and member count after Abort succeeds.");
            bool standardEditRetriesAbortAndRetainsReference =
                threadEditRollbackSource.Contains("TryAbortTransactionWithRetries(transaction, app, doc,") &&
                threadEditRollbackSource.Contains("\"StandardEdit\", threadId, stage, out abortFailure)") &&
                threadEditRollbackSource.Contains("if (abortSucceeded) transaction = null;") &&
                threadEditRollbackSource.Contains("originalEndOfPartRestored && modelSnapshotVerified &&") &&
                threadEditRollbackSource.Contains("ThreadBuilder.CaptureBuildRollbackSnapshotOrThrow(doc, \"ThreadEditService.StandardEdit\")") &&
                threadEditRollbackSource.Contains("ThreadBuilder.VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,");
            Check("ITERATION12-FIX7-FIX21-STANDARD-EDIT-RETRIES-ABORT-BEFORE-RECOVERY-CHECK",
                standardEditRetriesAbortAndRetainsReference, ref pass, ref fail,
                "Standard editing retries Abort up to three times and continues to require End-of-Part restoration plus saved group metadata/member count before confirming recovery.");
            bool editAndNativeAbortAttemptsAreAggregated =
                threadEditRollbackSource.Contains("var failures = new List<Exception>();") &&
                threadEditRollbackSource.Contains("new AggregateException(") &&
                threadEditRollbackSource.Contains("AbortAttemptsAggregate") &&
                threadEditRollbackSource.Contains("Transaction.Abort.retry.recovered") &&
                nativeConversionSource.Contains("var failures = new List<Exception>();") &&
                nativeConversionSource.Contains("new AggregateException(") &&
                nativeConversionSource.Contains("AbortAttemptsAggregate:") &&
                nativeConversionSource.Contains("Transaction.Abort.retry.recovered");
            Check("EDIT-AND-NATIVE-CONVERSION-ABORT-RETRIES-RETAIN-ALL-EXCEPTIONS",
                editAndNativeAbortAttemptsAreAggregated, ref pass, ref fail,
                "Every failed Abort attempt remains in an AggregateException when a later retry succeeds or all three attempts fail.");
            int nativeFinallyStart = nativeConversionSource.IndexOf(
                "if (transaction != null && !abortAttemptsCompleted)", StringComparison.Ordinal);
            int nativeFinallyEnd = nativeConversionSource.IndexOf(
                "private static bool TryAbortTransactionWithRetries(", nativeFinallyStart < 0 ? 0 : nativeFinallyStart,
                StringComparison.Ordinal);
            string nativeFinallySource = nativeFinallyStart >= 0 && nativeFinallyEnd > nativeFinallyStart
                ? nativeConversionSource.Substring(nativeFinallyStart, nativeFinallyEnd - nativeFinallyStart)
                : string.Empty;
            bool nativeFinallyUsesBoundedAbortAndFailsClosed =
                nativeFinallySource.Contains("TryAbortTransactionWithRetries(") &&
                nativeFinallySource.Contains("stage + ") &&
                nativeFinallySource.Contains(".finally") &&
                nativeFinallySource.Contains("rollbackVerificationCompleted=false; doNotSave=true") &&
                nativeFinallySource.Contains("criticalRollbackFailure = true;") &&
                !nativeFinallySource.Contains("transaction.Abort();");
            Check("NATIVE-CONVERSION-EMERGENCY-ABORT-IS-BOUNDED-AND-UNVERIFIED",
                nativeFinallyUsesBoundedAbortAndFailsClosed, ref pass, ref fail,
                "The emergency finally path uses the same bounded retry helper and never claims rollback verification when the normal verification path did not complete.");
            bool editRollbackRequiresMetadataAndMarker = nativeConversionSource.Contains("SourceFeatureExists(doc, plan)") &&
                !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8).Contains("ThreadEditSafetyPolicy.IsRollbackVerified(") &&
                File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8).Contains("originalEndOfPartRestored && modelSnapshotVerified &&") &&
                File.ReadAllText(Path.Combine(sourceRoot, "ThreadEditService.cs"), Encoding.UTF8).Contains("ThreadBuilder.VerifyBuildRollbackAfterAbort(app, doc, originalModelSnapshot,");
            Check("ITERATION12-FIX7-FIX16-EDIT-ROLLBACK-VERIFIES-METADATA-MEMBER-COUNT-AND-HISTORY",
                editRollbackRequiresMetadataAndMarker, ref pass, ref fail,
                "Edit rollback verification retains the End-of-Part restoration gate and compares the saved group record and member count before claiming recovery.");

            // Reuse the ThreadEditService source snapshot declared with the 2.7.168 BrowserPane contracts above.
            bool threadEditBrowserSearchReportsIncompleteReads =
                threadEditBrowserSource.Contains("FindThreadMemberInNode(BrowserNode node, string threadId, int depth,") &&
                threadEditBrowserSource.Contains("out bool traversalComplete)") &&
                threadEditBrowserSource.Contains("ThreadEditService.FindThreadMemberInNode.NativeObject.Depth=") &&
                threadEditBrowserSource.Contains("ThreadEditService.FindThreadMemberInNode.BrowserNodes.Depth=") &&
                threadEditBrowserSource.Contains("traversalComplete = false;");
            Check("THREAD-EDIT-BROWSER-MEMBER-SEARCH-REPORTS-INCOMPLETE-COM-READS",
                threadEditBrowserSearchReportsIncompleteReads, ref pass, ref fail,
                "Browser member search exposes traversal completeness and records NativeObject/BrowserNodes COM failures rather than silently treating them as absent members.");
            bool threadEditDownstreamSearchReportsIncompleteReads =
                threadEditBrowserSource.Contains("FindEndOfPartAnchorInNode(BrowserNode node, int depth,") &&
                threadEditBrowserSource.Contains("ThreadEditService.FindEndOfPartAnchorInNode.NativeObject.Depth=") &&
                threadEditBrowserSource.Contains("ThreadEditService.FindEndOfPartAnchorInNode.BrowserNodes.Depth=");
            Check("THREAD-EDIT-DOWNSTREAM-ANCHOR-SEARCH-REPORTS-INCOMPLETE-COM-READS",
                threadEditDownstreamSearchReportsIncompleteReads, ref pass, ref fail,
                "Downstream End-of-Part anchor discovery reports incomplete COM traversal and logs the precise failing browser-tree operation.");
            int originalTargetStart = threadEditBrowserSource.IndexOf(
                "internal static ThreadTarget ResolveOriginalTarget", StringComparison.Ordinal);
            // ResolveOriginalTarget now deduplicates plausible edges by canonical COM identity;
            // the old geometric-fallback helper was intentionally removed. End the extracted
            // method at its next declaration so the contract follows the current implementation.
            int originalTargetEnd = originalTargetStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static bool TryReadRecoveryEdgeCenter", originalTargetStart, StringComparison.Ordinal);
            string originalTargetMethod = originalTargetStart >= 0 && originalTargetEnd > originalTargetStart
                ? threadEditBrowserSource.Substring(originalTargetStart, originalTargetEnd - originalTargetStart)
                : string.Empty;
            int candidateAnalyzeStart = originalTargetMethod.IndexOf("ThreadTarget candidate;", StringComparison.Ordinal);
            int candidatePredicateStart = candidateAnalyzeStart < 0 ? -1 : originalTargetMethod.IndexOf(
                "if (candidate.IsExternal", candidateAnalyzeStart, StringComparison.Ordinal);
            string candidateAnalyzeBlock = candidateAnalyzeStart >= 0 && candidatePredicateStart > candidateAnalyzeStart
                ? originalTargetMethod.Substring(candidateAnalyzeStart, candidatePredicateStart - candidateAnalyzeStart)
                : string.Empty;
            bool originalTargetCandidateAnalysisFailsClosed =
                candidateAnalyzeBlock.Contains("catch (Exception ex)") &&
                candidateAnalyzeBlock.Contains("ThreadEditService.Error.OriginalEdgeAnalysisFailed") &&
                candidateAnalyzeBlock.Contains("ThreadEditService.ResolveOriginalTarget.CandidateAnalyze") &&
                candidateAnalyzeBlock.Contains("return null;") &&
                !candidateAnalyzeBlock.Contains("continue;");
            Check("THREAD-EDIT-ORIGINAL-TARGET-CANDIDATE-ANALYSIS-FAILS-CLOSED",
                originalTargetCandidateAnalysisFailsClosed, ref pass, ref fail,
                "A plausible edge that passes the saved cylinder/center prefilter but fails ThreadBuilder.Analyze makes the recovery scan incomplete; it cannot be skipped to manufacture a unique candidate.");
            int edgeCenterHelperStart = threadEditBrowserSource.IndexOf(
                "private static bool TryReadRecoveryEdgeCenter", StringComparison.Ordinal);
            int edgeCenterHelperEnd = edgeCenterHelperStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static bool AxisMatches", edgeCenterHelperStart, StringComparison.Ordinal);
            string edgeCenterHelperSource = edgeCenterHelperStart >= 0 && edgeCenterHelperEnd > edgeCenterHelperStart
                ? threadEditBrowserSource.Substring(edgeCenterHelperStart, edgeCenterHelperEnd - edgeCenterHelperStart)
                : string.Empty;
            // Search for the guarding condition from the start of the method.
            // Searching for "if (!..." from inside the call expression would always
            // fail because the prefix is lexically before TryReadRecoveryEdgeCenter(edge).
            int edgeCenterReadGuardStart = originalTargetMethod.IndexOf(
                "if (!TryReadRecoveryEdgeCenter(edge", StringComparison.Ordinal);
            int edgeCenterReadCallStart = edgeCenterReadGuardStart < 0 ? -1 : originalTargetMethod.IndexOf(
                "TryReadRecoveryEdgeCenter(edge", edgeCenterReadGuardStart, StringComparison.Ordinal);
            int edgeCenterFailureResource = edgeCenterReadGuardStart < 0 ? -1 : originalTargetMethod.IndexOf(
                "ThreadEditService.Error.OriginalEdgeAnalysisFailed", edgeCenterReadGuardStart, StringComparison.Ordinal);
            int edgeCenterFailureReturn = edgeCenterFailureResource < 0 ? -1 : originalTargetMethod.IndexOf(
                "return null;", edgeCenterFailureResource, StringComparison.Ordinal);
            int nextCandidateFilter = edgeCenterReadGuardStart < 0 ? -1 : originalTargetMethod.IndexOf(
                "if (!circularCandidate", edgeCenterReadGuardStart, StringComparison.Ordinal);
            int analyzeStart = threadBuilderSourceForTransactions.IndexOf(
                "public static ThreadTarget Analyze(Inventor.Application app, Edge edge)", StringComparison.Ordinal);
            int analyzeEnd = analyzeStart < 0 ? -1 : threadBuilderSourceForTransactions.IndexOf(
                "private static void AttachNativeTaperedMetadata", analyzeStart, StringComparison.Ordinal);
            string analyzeMethod = analyzeStart >= 0 && analyzeEnd > analyzeStart
                ? threadBuilderSourceForTransactions.Substring(analyzeStart, analyzeEnd - analyzeStart)
                : string.Empty;
            int cylinderRadiusReadStart = analyzeMethod.IndexOf(
                "Cylinder cyl = (Cylinder)cylFace.Geometry;", StringComparison.Ordinal);
            int cylinderRadiusReadEnd = cylinderRadiusReadStart < 0 ? -1 : analyzeMethod.IndexOf(
                "// far end of the face along the axis", cylinderRadiusReadStart, StringComparison.Ordinal);
            string cylinderRadiusReadBlock = cylinderRadiusReadStart >= 0 && cylinderRadiusReadEnd > cylinderRadiusReadStart
                ? analyzeMethod.Substring(cylinderRadiusReadStart, cylinderRadiusReadEnd - cylinderRadiusReadStart)
                : string.Empty;
            bool candidateAnalysisReadIsFailClosed =
                candidateAnalyzeBlock.Contains("ThreadBuilder.Analyze(app, edge)") &&
                candidateAnalyzeBlock.Contains("catch (Exception ex)") &&
                candidateAnalyzeBlock.Contains("ThreadEditService.ResolveOriginalTarget.CandidateAnalyze") &&
                candidateAnalyzeBlock.Contains("return null;") &&
                !candidateAnalyzeBlock.Contains("continue;");
            bool cylinderRadiusReadPropagatesFailure =
                cylinderRadiusReadBlock.Contains("cyl.Radius") &&
                cylinderRadiusReadBlock.Contains("radiusAtStart = cyl.Radius;") &&
                !cylinderRadiusReadBlock.Contains("catch") &&
                originalTargetMethod.Contains("Math.Abs(cylinder.Radius - snapshot.RadiusCm) > 0.001") &&
                originalTargetMethod.Contains("ThreadEditService.ResolveOriginalTarget") &&
                originalTargetMethod.Contains("OriginalCylinderRestoreFailed");
            bool originalTargetEdgeGeometryReadFailsClosed =
                edgeCenterReadGuardStart >= 0 && edgeCenterReadCallStart > edgeCenterReadGuardStart &&
                edgeCenterFailureResource > edgeCenterReadCallStart && edgeCenterFailureReturn > edgeCenterFailureResource &&
                (nextCandidateFilter < 0 || edgeCenterFailureReturn < nextCandidateFilter) &&
                originalTargetMethod.Contains("ThreadEditService.ResolveOriginalTarget.EdgeCenter") &&
                edgeCenterHelperSource.Contains("CurveTypeEnum geometryType = edge.GeometryType;") &&
                edgeCenterHelperSource.Contains("Circle circle = edge.Geometry as Circle;") &&
                edgeCenterHelperSource.Contains("Arc3d arc = edge.Geometry as Arc3d;") &&
                edgeCenterHelperSource.Contains("catch (Exception ex)") &&
                edgeCenterHelperSource.Contains("center = null;") &&
                edgeCenterHelperSource.Contains("failure = ex;") &&
                edgeCenterHelperSource.Contains("circularCandidate = true;") &&
                edgeCenterHelperSource.Contains("return false;") &&
                candidateAnalysisReadIsFailClosed && cylinderRadiusReadPropagatesFailure &&
                !originalTargetMethod.Contains("SameGeometricEdge") &&
                !edgeCenterHelperSource.Contains("return double.NaN;") &&
                !edgeCenterHelperSource.Contains("return 0.0;");
            Check("THREAD-EDIT-ORIGINAL-TARGET-EDGE-GEOMETRY-READ-FAILS-CLOSED",
                originalTargetEdgeGeometryReadFailsClosed, ref pass, ref fail,
                "A COM failure reading the candidate edge GeometryType/center or its supporting cylinder radius aborts original-target recovery; ThreadBuilder.Analyze exceptions are not skipped and no numeric radius fallback can manufacture a unique candidate.");
            string nativeTaperedBuilderSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeTaperedThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int generatedConeScanStart = nativeTaperedBuilderSource.IndexOf(
                "private static bool TryFindGeneratedCone", StringComparison.Ordinal);
            int generatedConeScanEnd = generatedConeScanStart < 0 ? -1 : nativeTaperedBuilderSource.IndexOf(
                "private static string DescribeRevolveSegments", generatedConeScanStart, StringComparison.Ordinal);
            string generatedConeScanMethod = generatedConeScanStart >= 0 && generatedConeScanEnd > generatedConeScanStart
                ? nativeTaperedBuilderSource.Substring(generatedConeScanStart, generatedConeScanEnd - generatedConeScanStart)
                : string.Empty;
            int coneSelectorStart = nativeTaperedBuilderSource.IndexOf(
                "private static bool TrySelectConeCandidate", StringComparison.Ordinal);
            int coneEdgeReaderStart = coneSelectorStart < 0 ? -1 : nativeTaperedBuilderSource.IndexOf(
                "private static bool TryReadConeCandidateEdge", coneSelectorStart, StringComparison.Ordinal);
            int oldEdgeCenterStart = coneEdgeReaderStart < 0 ? -1 : nativeTaperedBuilderSource.IndexOf(
                "private static double[] EdgeCenter", coneEdgeReaderStart, StringComparison.Ordinal);
            string coneSelectorSource = coneSelectorStart >= 0 && coneEdgeReaderStart > coneSelectorStart
                ? nativeTaperedBuilderSource.Substring(coneSelectorStart, coneEdgeReaderStart - coneSelectorStart)
                : string.Empty;
            string coneEdgeReaderSource = coneEdgeReaderStart >= 0 && oldEdgeCenterStart > coneEdgeReaderStart
                ? nativeTaperedBuilderSource.Substring(coneEdgeReaderStart, oldEdgeCenterStart - coneEdgeReaderStart)
                : string.Empty;
            bool nativeConeCandidateScanFailsClosed =
                generatedConeScanMethod.Contains("if (!string.IsNullOrWhiteSpace(faceScanError))") &&
                generatedConeScanMethod.Contains("return false;") &&
                generatedConeScanMethod.Contains("out selectionFailure") &&
                coneSelectorSource.Contains("if (!TryReadConeCandidateEdge(edge, out circular, out center, out radius, out edgeFailure))") &&
                coneSelectorSource.Contains("inspectionFailure =") &&
                coneSelectorSource.Contains("return false;") &&
                !coneSelectorSource.Contains("catch { continue; }") &&
                coneEdgeReaderSource.Contains("CurveTypeEnum geometryType = edge.GeometryType;") &&
                coneEdgeReaderSource.Contains("edge.Geometry") &&
                coneEdgeReaderSource.Contains("!Finite(radius) || radius <= 0.0") &&
                coneEdgeReaderSource.Contains("catch (Exception ex)") &&
                coneEdgeReaderSource.Contains("return false;");
            Check("NATIVE-TAPER-CONE-CANDIDATE-SCAN-FAILS-CLOSED-ON-COM-READ-ERROR",
                nativeConeCandidateScanFailsClosed, ref pass, ref fail,
                "A partial face scan or unreadable candidate face/edge must abort generated-cone selection; geometry-read failures cannot be skipped to manufacture one apparently unique cone.");
            bool threadEditSnapshotRejectsUnreadableTopNode =
                threadEditBrowserSource.Contains("ThreadEditService.TryCaptureEndOfPartPosition.TopNodeNativeObject.Index=") &&
                threadEditBrowserSource.Contains("error = HelivantaLocalization.Get(\"ThreadEditService.Error.EndOfPartSnapshotFailed\");") &&
                threadEditBrowserSource.Contains("return false;");
            Check("THREAD-EDIT-EOP-SNAPSHOT-FAILS-CLOSED-ON-UNREADABLE-TOP-LEVEL-NODE",
                threadEditSnapshotRejectsUnreadableTopNode, ref pass, ref fail,
                "If the top-level BrowserNode cannot be read, End-of-Part position is not guessed and the edit is rejected before the transaction starts.");
            bool threadEditSnapshotRequiresCompleteThreadSearch =
                threadEditBrowserSource.Contains("if (!threadTraversalComplete)") &&
                threadEditBrowserSource.Contains("Thread-member search was incomplete; no End-of-Part snapshot was accepted.");
            Check("THREAD-EDIT-EOP-SNAPSHOT-REQUIRES-COMPLETE-THREAD-SEARCH",
                threadEditSnapshotRequiresCompleteThreadSearch, ref pass, ref fail,
                "A thread member hidden by a failed COM read cannot be mistaken for an unrelated downstream feature.");
            bool threadEditSnapshotRequiresCompleteDownstreamSearch =
                threadEditBrowserSource.Contains("if (!downstreamTraversalComplete)") &&
                threadEditBrowserSource.Contains("Downstream-anchor search was incomplete; no End-of-Part snapshot was accepted.");
            Check("THREAD-EDIT-EOP-SNAPSHOT-REQUIRES-COMPLETE-DOWNSTREAM-SEARCH",
                threadEditSnapshotRequiresCompleteDownstreamSearch, ref pass, ref fail,
                "An incomplete search cannot supply a guessed downstream restoration anchor.");
            string threadMetadataStoreSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadMetadataStore.cs"), Encoding.UTF8)
                : string.Empty;
            int metadataAttachStart = threadMetadataStoreSource.IndexOf(
                "private static bool TryAttachRecord", StringComparison.Ordinal);
            int metadataAttachEnd = metadataAttachStart < 0 ? -1 : threadMetadataStoreSource.IndexOf(
                "private static bool TryReadMember", metadataAttachStart, StringComparison.Ordinal);
            string metadataAttachMethodSource = metadataAttachStart >= 0 && metadataAttachEnd > metadataAttachStart
                ? threadMetadataStoreSource.Substring(metadataAttachStart, metadataAttachEnd - metadataAttachStart)
                : string.Empty;
            int attemptedOwnerTrackPosition = metadataAttachMethodSource.IndexOf("attemptedOwners.Add(owner)", StringComparison.Ordinal);
            int attributeSetCreatePosition = metadataAttachMethodSource.IndexOf("sets.Add(AttributeSetName, false)", StringComparison.Ordinal);
            Check("THREAD-METADATA-ATTACH-TRACKS-OWNER-BEFORE-ATTRIBUTESET-CREATE",
                attemptedOwnerTrackPosition >= 0 && attributeSetCreatePosition > attemptedOwnerTrackPosition &&
                metadataAttachMethodSource.Contains("COM can create the named set and") &&
                !metadataAttachMethodSource.Contains("taggedObjects.Add(owner)"),
                ref pass, ref fail,
                "The current owner is included in cleanup before COM creates the AttributeSet, covering an Add call that creates the set and then throws.");
            int metadataRemoveSetStart = threadMetadataStoreSource.IndexOf(
                "private static bool TryRemoveSet", StringComparison.Ordinal);
            int metadataRemoveSetEnd = metadataRemoveSetStart < 0 ? -1 : threadMetadataStoreSource.IndexOf(
                "private static double[] CopyVector", metadataRemoveSetStart, StringComparison.Ordinal);
            string metadataRemoveSetMethodSource = metadataRemoveSetStart >= 0 && metadataRemoveSetEnd > metadataRemoveSetStart
                ? threadMetadataStoreSource.Substring(metadataRemoveSetStart, metadataRemoveSetEnd - metadataRemoveSetStart)
                : string.Empty;
            Check("THREAD-METADATA-ATTACH-ROLLBACK-VERIFIES-ATTRIBUTESET-REMOVAL",
                metadataRemoveSetMethodSource.Contains("private static bool TryRemoveSet") &&
                metadataRemoveSetMethodSource.Contains("set.Delete()") &&
                metadataRemoveSetMethodSource.Contains("bool removed = !sets.NameIsUsed[AttributeSetName]") &&
                metadataRemoveSetMethodSource.Contains("return false;"),
                ref pass, ref fail,
                "Metadata cleanup reports success only after confirming that the named AttributeSet is absent; COM/delete failures remain explicit failures.");
            Check("THREAD-METADATA-ATTACH-ROLLBACK-FAILURES-ARE-SUMMARIZED",
                metadataAttachMethodSource.Contains("if (!TryRemoveSet(attemptedOwners[i], i)) cleanupFailedIndexes.Add(i)") &&
                metadataAttachMethodSource.Contains("ThreadMetadataStore.TryAttachRecord.RollbackSummary") &&
                metadataAttachMethodSource.Contains("cleanupComplete=") &&
                metadataAttachMethodSource.Contains("cleanupFailureCount=") &&
                metadataAttachMethodSource.Contains("cleanupFailedIndexes.Take(32)") &&
                metadataAttachMethodSource.Contains("cleanupFailedIndexesTruncated=") &&
                metadataAttachMethodSource.Contains("enclosingTransactionMustAbort=true"),
                ref pass, ref fail,
                "Any metadata cleanup failure is recorded with affected object indexes and an explicit requirement to abort the enclosing transaction.");
            bool metadataSelectionRejectsPartialThreadIdTraversal =
                threadMetadataStoreSource.Contains("CollectThreadIdsFromBrowserNode(node, ids, 0, out traversalComplete)") &&
                threadMetadataStoreSource.Contains("if (!traversalComplete)") &&
                threadMetadataStoreSource.Contains("a partial ThreadId set was rejected.");
            Check("THREAD-METADATA-SELECTION-REJECTS-PARTIAL-THREADID-COM-TRAVERSAL",
                metadataSelectionRejectsPartialThreadIdTraversal, ref pass, ref fail,
                "The selected folder cannot be accepted as a single thread when COM traversal may have hidden additional ThreadIds.");
            int selectedNodeWalkStart = threadMetadataStoreSource.IndexOf(
                "private static void CollectSelectedBrowserNodes", StringComparison.Ordinal);
            int selectedNodeWalkEnd = selectedNodeWalkStart < 0 ? -1 : threadMetadataStoreSource.IndexOf(
                "internal static bool TryGetThreadId(object owner", selectedNodeWalkStart, StringComparison.Ordinal);
            string selectedNodeWalkSource = selectedNodeWalkStart >= 0 && selectedNodeWalkEnd > selectedNodeWalkStart
                ? threadMetadataStoreSource.Substring(selectedNodeWalkStart, selectedNodeWalkEnd - selectedNodeWalkStart)
                : string.Empty;
            int singleSelectedNodeMethodStart = threadMetadataStoreSource.IndexOf(
                "internal static bool TryGetSingleSelectedBrowserNode", StringComparison.Ordinal);
            int singleSelectedNodeMethodEnd = singleSelectedNodeMethodStart < 0 ? -1 : threadMetadataStoreSource.IndexOf(
                "internal static bool TryGetSelectedBrowserNodes(", singleSelectedNodeMethodStart, StringComparison.Ordinal);
            string singleSelectedNodeMethodSource = singleSelectedNodeMethodStart >= 0 && singleSelectedNodeMethodEnd > singleSelectedNodeMethodStart
                ? threadMetadataStoreSource.Substring(singleSelectedNodeMethodStart, singleSelectedNodeMethodEnd - singleSelectedNodeMethodStart)
                : string.Empty;
            int selectedReadStart = selectedNodeWalkSource.IndexOf("try { if (node.Selected) selected.Add(node); }", StringComparison.Ordinal);
            int selectedReadCatch = selectedReadStart < 0 ? -1 : selectedNodeWalkSource.IndexOf("catch (Exception ex)", selectedReadStart, StringComparison.Ordinal);
            int selectedReadDiagnostic = selectedReadCatch < 0 ? -1 : selectedNodeWalkSource.IndexOf(
                "ThreadMetadataStore.CollectSelectedBrowserNodes.Selected", selectedReadCatch, StringComparison.Ordinal);
            string selectedReadFailureBlock = selectedReadCatch >= 0 && selectedReadDiagnostic > selectedReadCatch
                ? selectedNodeWalkSource.Substring(selectedReadCatch, selectedReadDiagnostic - selectedReadCatch)
                : string.Empty;
            int childEnumerationStart = selectedNodeWalkSource.IndexOf("foreach (BrowserNode child in node.BrowserNodes)", StringComparison.Ordinal);
            int childEnumerationCatch = childEnumerationStart < 0 ? -1 : selectedNodeWalkSource.IndexOf(
                "catch (Exception ex)", childEnumerationStart, StringComparison.Ordinal);
            int childEnumerationDiagnostic = childEnumerationCatch < 0 ? -1 : selectedNodeWalkSource.IndexOf(
                "ThreadMetadataStore.CollectSelectedBrowserNodes.BrowserNodes", childEnumerationCatch, StringComparison.Ordinal);
            string childEnumerationFailureBlock = childEnumerationCatch >= 0 && childEnumerationDiagnostic > childEnumerationCatch
                ? selectedNodeWalkSource.Substring(childEnumerationCatch, childEnumerationDiagnostic - childEnumerationCatch)
                : string.Empty;
            bool metadataSelectionRequiresCompleteSelectedNodeWalk =
                selectedReadStart >= 0 && selectedReadFailureBlock.Contains("traversalComplete = false;") &&
                selectedReadDiagnostic > selectedReadCatch &&
                childEnumerationStart > selectedReadDiagnostic &&
                childEnumerationCatch > childEnumerationStart && childEnumerationFailureBlock.Contains("traversalComplete = false;") &&
                childEnumerationDiagnostic > childEnumerationCatch &&
                selectedNodeWalkSource.Contains("CollectSelectedBrowserNodes(child, selected, depth + 1, out childComplete)") &&
                selectedNodeWalkSource.Contains("if (!childComplete) traversalComplete = false;") &&
                selectedNodeWalkSource.Contains("if (depth > 64 || selected.Count > 64)") &&
                selectedNodeWalkSource.Contains("if (node == null || selected == null)") &&
                selectedNodeWalkSource.Contains("ThreadMetadataStore.CollectSelectedBrowserNodes.NullInput") &&
                selectedNodeWalkSource.Contains("traversalComplete = false;") &&
                singleSelectedNodeMethodSource.Contains("rootNode = pane.TopNode") &&
                singleSelectedNodeMethodSource.Contains("CollectSelectedBrowserNodes(rootNode, selected, 0, out traversalComplete)") &&
                singleSelectedNodeMethodSource.Split(new[] { "pane.TopNode" }, StringSplitOptions.None).Length - 1 == 1 &&
                singleSelectedNodeMethodSource.Contains("if (!traversalComplete)") &&
                singleSelectedNodeMethodSource.Contains("return false;") &&
                singleSelectedNodeMethodSource.Contains("if (selected.Count != 1)");
            Check("THREAD-METADATA-SELECTION-REQUIRES-COMPLETE-SELECTED-NODE-WALK",
                metadataSelectionRequiresCompleteSelectedNodeWalk, ref pass, ref fail,
                "Exactly one selected BrowserNode is accepted only after selection flags, child collections and recursive results are read successfully; partial traversal is rejected.");
            int loadAllGroupsStart = threadMetadataStoreSource.IndexOf(
                "internal static bool TryLoadAllGroups", StringComparison.Ordinal);
            int loadAllGroupsEnd = loadAllGroupsStart < 0 ? -1 : threadMetadataStoreSource.IndexOf(
                "internal static bool TryGetThreadIdFromSelection", loadAllGroupsStart, StringComparison.Ordinal);
            string loadAllGroupsSource = loadAllGroupsStart >= 0 && loadAllGroupsEnd > loadAllGroupsStart
                ? threadMetadataStoreSource.Substring(loadAllGroupsStart, loadAllGroupsEnd - loadAllGroupsStart)
                : string.Empty;
            string orphanedMetadataRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadMetadataStore.Error.OrphanedMetadataSet", CultureInfo.InvariantCulture);
            string orphanedMetadataEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadMetadataStore.Error.OrphanedMetadataSet", CultureInfo.InvariantCulture);
            bool orphanedMetadataIsRejected =
                loadAllGroupsSource.Contains("FindObjects(AttributeSetName, \"*\", null)") &&
                loadAllGroupsSource.Contains("TryGetThreadId(owner, out id)") &&
                loadAllGroupsSource.Contains("ThreadMetadataStore.Error.OrphanedMetadataSet") &&
                loadAllGroupsSource.Contains("return false;") &&
                !string.IsNullOrWhiteSpace(orphanedMetadataRu) &&
                !string.IsNullOrWhiteSpace(orphanedMetadataEn) &&
                !orphanedMetadataEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("THREAD-METADATA-ENUMERATION-INCLUDES-ORPHANED-SETS-WITHOUT-THREADID",
                orphanedMetadataIsRejected, ref pass, ref fail,
                "All objects carrying any HelivantaThreadMeta attribute are inspected; a missing/unreadable ThreadId has a complete RU/EN refusal instead of being silently omitted from group integrity checks.");
            Check("THREAD-METADATA-EMPTY-SETS-SCAN-ALL-PLUGIN-OWNER-COLLECTIONS",
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.Features, \"PartFeatures\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.WorkPlanes, \"WorkPlanes\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.Sketches, \"PlanarSketches\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.Sketches3D, \"Sketches3D\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.WorkAxes, \"WorkAxes\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.WorkPoints, \"WorkPoints\")") &&
                threadMetadataStoreSource.Contains("FindEmptyHelivantaMetadataSetInCollection(definition.WorkSurfaces, \"WorkSurfaces\")"),
                ref pass, ref fail,
                "Empty sets are checked on the three owner families currently tagged by the add-in plus supported 3D sketch/work-feature collections.");
            int emptySetScanPosition = loadAllGroupsSource.IndexOf(
                "TryFindEmptyHelivantaMetadataSet(doc, out emptyMetadataOwner, out ownerScanError)", StringComparison.Ordinal);
            int populatedSetQueryPosition = loadAllGroupsSource.IndexOf(
                "FindObjects(AttributeSetName, \"*\", null)", StringComparison.Ordinal);
            Check("THREAD-METADATA-EMPTY-ATTRIBUTE-SETS-ARE-REJECTED",
                threadMetadataStoreSource.Contains("if (metadataSet.Count == 0)") &&
                emptySetScanPosition >= 0 && populatedSetQueryPosition > emptySetScanPosition &&
                threadMetadataStoreSource.Contains("ThreadMetadataStore.Error.EmptyMetadataSet") &&
                threadMetadataStoreSource.Contains("ThreadMetadataStore.TryLoadAllGroups.EmptyMetadataSet"),
                ref pass, ref fail,
                "A Helivanta AttributeSet with zero attributes blocks group-changing operations instead of being invisible to FindObjects.");
            string emptySetErrorRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadMetadataStore.Error.EmptyMetadataSet", CultureInfo.InvariantCulture);
            string emptySetErrorEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadMetadataStore.Error.EmptyMetadataSet", CultureInfo.InvariantCulture);
            string ownerScanErrorRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadMetadataStore.Error.OwnerScanFailed", CultureInfo.InvariantCulture);
            string ownerScanErrorEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadMetadataStore.Error.OwnerScanFailed", CultureInfo.InvariantCulture);
            Check("THREAD-METADATA-EMPTY-SET-REFUSALS-HAVE-RU-EN-GUIDANCE",
                !string.IsNullOrWhiteSpace(emptySetErrorRu) && !string.IsNullOrWhiteSpace(emptySetErrorEn) &&
                !string.IsNullOrWhiteSpace(ownerScanErrorRu) && !string.IsNullOrWhiteSpace(ownerScanErrorEn) &&
                !emptySetErrorEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !ownerScanErrorEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail,
                "Empty-set detection and incomplete owner-scan failures provide complete English and Russian messages.");
            bool threadEditRestorationAnchorRejectsIncompleteTraversal =
                threadEditBrowserSource.Contains("FindFirstThreadHistoryAnchor(PartDocument doc, string threadId)") &&
                threadEditBrowserSource.Contains("if (!traversalComplete)") &&
                threadEditBrowserSource.Contains("Не удалось полностью прочитать дерево построений для восстановления якоря резьбы.");
            Check("THREAD-EDIT-RESTORE-END-OF-PART-REQUIRES-COMPLETE-ANCHOR-SEARCH",
                threadEditRestorationAnchorRejectsIncompleteTraversal, ref pass, ref fail,
                "Rollback restoration throws when the thread anchor cannot be verified from a complete browser-tree traversal.");
            bool threadEditFolderRenameRejectsIncompleteSearch =
                threadEditBrowserSource.Contains("if (!traversalComplete)") &&
                threadEditBrowserSource.Contains("ThreadEditService.TryRefreshThreadFolderName") &&
                threadEditBrowserSource.Contains("browser-folder rename was not attempted.");
            Check("THREAD-EDIT-BROWSER-FOLDER-RENAME-REJECTS-INCOMPLETE-MEMBER-SEARCH",
                threadEditFolderRenameRejectsIncompleteSearch, ref pass, ref fail,
                "Folder renaming is skipped when COM traversal cannot establish which folder owns the current ThreadId.");

            bool threadEditFolderUniquenessFailsClosedOnIncompleteScan =
                threadEditBrowserSource.Contains("TryGetUniqueFolderNameExcluding(BrowserNode topNode, string desiredName,") &&
                threadEditBrowserSource.Contains("TryGetUniqueFolderNameExcluding.Enumeration") &&
                threadEditBrowserSource.Contains("if (!TryGetUniqueFolderNameExcluding(topNode, desiredName, selectedFolder,") &&
                threadEditBrowserSource.Contains("ThreadEditService.TryGetUniqueFolderNameExcluding.FolderName") &&
                !threadEditBrowserSource.Contains("private static string UniqueFolderNameExcluding(");
            Check("THREAD-EDIT-BROWSER-FOLDER-UNIQUENESS-REQUIRES-COMPLETE-SCAN",
                threadEditFolderUniquenessFailsClosedOnIncompleteScan, ref pass, ref fail,
                "A COM enumeration or folder-name read failure aborts rename instead of guessing a potentially colliding unique name.");
            int packageEditCleanupCommit = threadEditBrowserSource.IndexOf("transaction.End();", StringComparison.Ordinal);
            int packageEditCleanupCall = threadEditBrowserSource.IndexOf("CleanupEmptyThreadFolders(doc);", StringComparison.Ordinal);
            int standardEditMethodStartForCleanup = threadEditBrowserSource.IndexOf(
                "internal static bool TryReplace(Inventor.Application app, string threadId, ThreadParams replacement,", StringComparison.Ordinal);
            int standardEditCleanupCommit = standardEditMethodStartForCleanup < 0 ? -1 :
                threadEditBrowserSource.IndexOf("transaction.End();", standardEditMethodStartForCleanup, StringComparison.Ordinal);
            int standardEditCleanupCall = standardEditMethodStartForCleanup < 0 ? -1 :
                threadEditBrowserSource.IndexOf("CleanupEmptyThreadFolders(doc);", standardEditMethodStartForCleanup, StringComparison.Ordinal);
            int standardEditCleanupCallAfterCommit = standardEditCleanupCommit < 0 ? -1 :
                threadEditBrowserSource.IndexOf("CleanupEmptyThreadFolders(doc);", standardEditCleanupCommit + "transaction.End();".Length, StringComparison.Ordinal);
            bool optionalCleanupRunsWithinTheCorrectTransaction = packageEditCleanupCommit >= 0 &&
                packageEditCleanupCall > packageEditCleanupCommit && standardEditCleanupCommit >= 0 &&
                standardEditCleanupCall > standardEditMethodStartForCleanup && standardEditCleanupCall < standardEditCleanupCommit &&
                standardEditCleanupCallAfterCommit > standardEditCleanupCommit &&
                threadEditBrowserSource.Contains("if (replacement.Tapered)") &&
                threadEditBrowserSource.Contains("if (!replacement.Tapered)");
            int folderEmptinessHelperStart = threadEditBrowserSource.IndexOf(
                "private static bool TryGetBrowserFolderNodeEmptiness(BrowserNode node, string diagnosticContext,", StringComparison.Ordinal);
            int folderEmptinessHelperEnd = folderEmptinessHelperStart < 0 ? -1 :
                threadEditBrowserSource.IndexOf("private static void CleanupEmptyManagedNestedFolders(", folderEmptinessHelperStart, StringComparison.Ordinal);
            string folderEmptinessHelper = folderEmptinessHelperStart >= 0 && folderEmptinessHelperEnd > folderEmptinessHelperStart
                ? threadEditBrowserSource.Substring(folderEmptinessHelperStart, folderEmptinessHelperEnd - folderEmptinessHelperStart)
                : string.Empty;
            int folderCleanupMethodStart = threadEditBrowserSource.IndexOf(
                "private static void CleanupEmptyThreadFolders(PartDocument doc)", StringComparison.Ordinal);
            int folderCleanupMethodEnd = folderCleanupMethodStart < 0 ? -1 :
                threadEditBrowserSource.IndexOf("private static ThreadEditMemberKind ClassifyMemberForDeletion", folderCleanupMethodStart, StringComparison.Ordinal);
            string folderCleanupMethod = folderCleanupMethodStart >= 0 && folderCleanupMethodEnd > folderCleanupMethodStart
                ? threadEditBrowserSource.Substring(folderCleanupMethodStart, folderCleanupMethodEnd - folderCleanupMethodStart)
                : string.Empty;
            bool folderCleanupReadsBothCollectionsAndLogsFailures =
                folderEmptinessHelper.Contains("node.BrowserNodes") && folderEmptinessHelper.Contains("browserNodes.Count") &&
                folderEmptinessHelper.Contains("node.BrowserFolders") && folderEmptinessHelper.Contains("browserFolders.Count") &&
                folderEmptinessHelper.Contains("LogSuppressedExceptionForDiagnostics") &&
                folderEmptinessHelper.Contains("return false;");
            int threadEditManagedFolderDeleteStart = threadEditBrowserSource.IndexOf(
                "private static bool TryDeleteManagedEmptyFolder(BrowserFolder folder, string diagnosticContext)", StringComparison.Ordinal);
            int threadEditManagedFolderDeleteEnd = threadEditManagedFolderDeleteStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static void CleanupEmptyThreadFolders(PartDocument doc)", threadEditManagedFolderDeleteStart, StringComparison.Ordinal);
            string threadEditManagedFolderDeleteMethod = threadEditManagedFolderDeleteStart >= 0 && threadEditManagedFolderDeleteEnd > threadEditManagedFolderDeleteStart
                ? threadEditBrowserSource.Substring(threadEditManagedFolderDeleteStart, threadEditManagedFolderDeleteEnd - threadEditManagedFolderDeleteStart)
                : string.Empty;
            bool threadEditCleanupNeverDeletesUnreadableFolder =
                folderCleanupReadsBothCollectionsAndLogsFailures &&
                threadEditManagedFolderDeleteMethod.Contains("ThreadBrowserFolderPolicy.IsManagedThreadFolder(name)") &&
                threadEditManagedFolderDeleteMethod.Contains("TryGetBrowserFolderNodeEmptiness(node, context + \".EmptinessCheck\", out isEmpty)") &&
                threadEditManagedFolderDeleteMethod.Contains("if (!TryGetBrowserFolderNodeEmptiness(node, context + \".EmptinessCheck\", out isEmpty) || !isEmpty)") &&
                threadEditManagedFolderDeleteMethod.Contains("LogSuppressedExceptionForDiagnostics(context + \".Delete\", ex)") &&
                folderCleanupMethod.Contains("ThreadEditService.CleanupEmptyThreadFolders.PaneEnumeration") &&
                folderCleanupMethod.Contains("ThreadEditService.CleanupEmptyThreadFolders.RootEnumeration") &&
                folderCleanupMethod.Contains("TryDeleteManagedEmptyFolder(folder, ") &&
                optionalCleanupRunsWithinTheCorrectTransaction;
            Check("THREAD-EDIT-EMPTY-FOLDER-CLEANUP-IS-DIAGNOSTIC-AND-FAIL-SAFE",
                threadEditCleanupNeverDeletesUnreadableFolder, ref pass, ref fail,
                "COM failures while reading either BrowserNodes or BrowserFolders are logged; unreadable folders are preserved; root folders are rechecked immediately before deletion; tapered cleanup is part of the edit transaction for exact Undo, while non-tapered cleanup remains post-commit.");

            int folderSnapshotStart = threadEditBrowserSource.IndexOf(
                "private static ThreadFolderSnapshotOutcome FindThreadFolderNameForGroup(", StringComparison.Ordinal);
            int folderSnapshotEnd = folderSnapshotStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static bool TryRefreshThreadFolderName(", folderSnapshotStart, StringComparison.Ordinal);
            string folderSnapshotMethod = folderSnapshotStart >= 0 && folderSnapshotEnd > folderSnapshotStart
                ? threadEditBrowserSource.Substring(folderSnapshotStart, folderSnapshotEnd - folderSnapshotStart)
                : string.Empty;
            bool threadEditOriginalFolderSnapshotReportsEveryIncompleteBoundary =
                folderSnapshotMethod.Contains("ThreadEditService.FindThreadFolderNameForGroup.TopNode") &&
                folderSnapshotMethod.Contains("ThreadEditService.FindThreadFolderNameForGroup.FolderName") &&
                folderSnapshotMethod.Contains("ThreadEditService.FindThreadFolderNameForGroup.BrowserNode") &&
                folderSnapshotMethod.Contains("ThreadEditService.FindThreadFolderNameForGroup.Enumeration") &&
                folderSnapshotMethod.Contains("if (!traversalComplete)") &&
                folderSnapshotMethod.Contains("ThreadFolderSnapshotOutcome.Incomplete") &&
                folderSnapshotMethod.Contains("names.Count > 1") &&
                !folderSnapshotMethod.Contains("catch { return null; }");
            Check("THREAD-EDIT-ORIGINAL-FOLDER-SNAPSHOT-DISTINGUISHES-INCOMPLETE-AND-AMBIGUOUS",
                threadEditOriginalFolderSnapshotReportsEveryIncompleteBoundary, ref pass, ref fail,
                "The optional pre-edit folder snapshot reports COM read/enumeration failures and ambiguous ownership instead of silently collapsing every outcome to null.");

            bool threadEditSavedNameFallbackRequiresVerifiedSnapshot =
                threadEditBrowserSource.Contains("private enum ThreadFolderSnapshotOutcome") &&
                threadEditBrowserSource.Contains("ThreadFolderSnapshotOutcome originalThreadFolderSnapshotOutcome =") &&
                threadEditBrowserSource.Contains("originalFolderSnapshotOutcome == ThreadFolderSnapshotOutcome.Found") &&
                threadEditBrowserSource.Contains("originalFolderSnapshotOutcome == ThreadFolderSnapshotOutcome.Incomplete") &&
                threadEditBrowserSource.Contains("originalFolderSnapshotOutcome == ThreadFolderSnapshotOutcome.Ambiguous") &&
                threadEditBrowserSource.Contains("saved-name fallback was refused.");
            Check("THREAD-EDIT-SAVED-FOLDER-NAME-FALLBACK-REQUIRES-COMPLETE-PRE-EDIT-THREADID-SCAN",
                threadEditSavedNameFallbackRequiresVerifiedSnapshot, ref pass, ref fail,
                "A saved folder name is eligible for post-edit fallback only when exactly one folder was identified by ThreadId in a complete pre-edit scan.");

            int folderRefreshStart = threadEditBrowserSource.IndexOf(
                "private static bool TryRefreshThreadFolderName(", StringComparison.Ordinal);
            int folderRefreshEnd = folderRefreshStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static bool TryGetUniqueFolderNameExcluding(", folderRefreshStart, StringComparison.Ordinal);
            string folderRefreshMethod = folderRefreshStart >= 0 && folderRefreshEnd > folderRefreshStart
                ? threadEditBrowserSource.Substring(folderRefreshStart, folderRefreshEnd - folderRefreshStart)
                : string.Empty;
            int guardedTopNodeRead = folderRefreshMethod.IndexOf("try { topNode = pane.TopNode; }", StringComparison.Ordinal);
            int guardedTopNodeCatch = guardedTopNodeRead < 0 ? -1 : folderRefreshMethod.IndexOf(
                "catch (Exception ex)", guardedTopNodeRead, StringComparison.Ordinal);
            int folderRefreshEnumeration = folderRefreshMethod.IndexOf(
                "foreach (BrowserFolder folder in topNode.BrowserFolders)", StringComparison.Ordinal);
            bool threadEditOptionalRenameGuardsTopNodeComRead =
                guardedTopNodeRead >= 0 && guardedTopNodeCatch > guardedTopNodeRead &&
                folderRefreshEnumeration > guardedTopNodeCatch &&
                folderRefreshMethod.Contains("ThreadEditService.TryRefreshThreadFolderName.TopNode") &&
                folderRefreshMethod.Contains("TryGetUniqueFolderNameExcluding(topNode, desiredName, selectedFolder,");
            Check("THREAD-EDIT-OPTIONAL-FOLDER-RENAME-GUARDS-TOPNODE-COM-READ",
                threadEditOptionalRenameGuardsTopNodeComRead, ref pass, ref fail,
                "A COM failure while obtaining the post-edit BrowserPane.TopNode becomes a folder-rename warning, not an exception that aborts the completed geometry edit.");
            bool threadEditSavedNameFallbackEnumerationIsNonFatal =
                folderRefreshMethod.Contains("ThreadEditService.TryRefreshThreadFolderName.OriginalFolderEnumeration") &&
                folderRefreshMethod.Contains("foreach (BrowserFolder folder in topNode.BrowserFolders)") &&
                folderRefreshMethod.Contains("error = HelivantaLocalization.Get(\"ThreadEditService.Error.BrowserFolderLookupFailed\")") &&
                folderRefreshMethod.Contains("originalFolderSnapshotOutcome == ThreadFolderSnapshotOutcome.Found");
            Check("THREAD-EDIT-SAVED-NAME-FALLBACK-ENUMERATION-IS-NONFATAL",
                threadEditSavedNameFallbackEnumerationIsNonFatal, ref pass, ref fail,
                "A COM failure enumerating the saved-name fallback is converted to a logged rename failure and cannot escape into the model-edit transaction.");

            int browserPaneStart = threadEditBrowserSource.IndexOf(
                "private static BrowserPane FindTreeBrowserPane(", StringComparison.Ordinal);
            int browserPaneEnd = browserPaneStart < 0 ? -1 : threadEditBrowserSource.IndexOf(
                "private static object FindThreadMemberInNode(", browserPaneStart, StringComparison.Ordinal);
            string browserPaneMethod = browserPaneStart >= 0 && browserPaneEnd > browserPaneStart
                ? threadEditBrowserSource.Substring(browserPaneStart, browserPaneEnd - browserPaneStart)
                : string.Empty;
            int activePaneRead = browserPaneMethod.IndexOf("panes.ActivePane", StringComparison.Ordinal);
            int activePaneFallbackCatch = activePaneRead < 0 ? -1 : browserPaneMethod.IndexOf("catch (Exception ex)", activePaneRead, StringComparison.Ordinal);
            int activePaneFallbackDiagnostic = activePaneFallbackCatch < 0 ? -1 : browserPaneMethod.IndexOf(
                "ThreadEditService.FindTreeBrowserPane.ActivePaneFallback", activePaneFallbackCatch, StringComparison.Ordinal);
            int paneEnumerationFallback = activePaneFallbackDiagnostic < 0 ? -1 : browserPaneMethod.IndexOf(
                "foreach (BrowserPane pane in panes)", activePaneFallbackDiagnostic, StringComparison.Ordinal);
            string activePaneFallbackCatchSource = activePaneFallbackCatch >= 0 && activePaneFallbackDiagnostic > activePaneFallbackCatch
                ? browserPaneMethod.Substring(activePaneFallbackCatch, activePaneFallbackDiagnostic - activePaneFallbackCatch)
                : string.Empty;
            bool threadEditBrowserPaneFallbackIsRetainedAndLogged =
                browserPaneMethod.Contains("panes = doc.BrowserPanes") &&
                browserPaneMethod.Split(new[] { "doc.BrowserPanes" }, StringSplitOptions.None).Length - 1 == 1 &&
                activePaneRead >= 0 && activePaneFallbackCatch > activePaneRead &&
                activePaneFallbackDiagnostic > activePaneFallbackCatch &&
                !activePaneFallbackCatchSource.Contains("return;") &&
                paneEnumerationFallback > activePaneFallbackDiagnostic &&
                browserPaneMethod.Contains("ThreadEditService.FindTreeBrowserPane.PaneEnumeration");
            Check("THREAD-EDIT-BROWSER-PANE-ACTIVE-PANE-FAILURE-RETAINS-ENUMERATION-FALLBACK",
                threadEditBrowserPaneFallbackIsRetainedAndLogged, ref pass, ref fail,
                "An ActivePane COM failure is diagnosed but does not suppress the subsequent BrowserPanes enumeration fallback.");

            string packagingPreviewSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadPreviewControlsDialog.cs"), Encoding.UTF8)
                : string.Empty;
            bool packagingPreviewMatchesStandardShell =
                packagingPreviewSource.Contains("Text = \"Управление предпросмотром\"") &&
                packagingPreviewSource.Contains("FormBorderStyle = FormBorderStyle.FixedToolWindow") &&
                packagingPreviewSource.Contains("ClientSize = new Size(660, 576)") &&
                packagingPreviewSource.Contains("LoadBrandLogo()") &&
                packagingPreviewSource.Contains("LoadEmbeddedIcon()");
            Check("ITERATION12-FIX7-FIX20-CAP-NECK-PREVIEW-MATCHES-STANDARD-PALETTE-SHELL",
                packagingPreviewMatchesStandardShell, ref pass, ref fail,
                "Cap/neck preview shares the standard palette's fixed window size, border behavior, icon and theme-aware Helivanta wordmark while keeping its own content.");
            bool packagingPreviewUsesEngineeringGridAndRoundedPcoProfile =
                packagingPreviewSource.Contains("g.DrawLine(major ? gridMajor : grid") &&
                packagingPreviewSource.Contains("path.AddBezier(") &&
                packagingPreviewSource.Contains("PCO-1881 Detail B bell profile") &&
                !packagingPreviewSource.Contains("PointF[] tooth = new PointF[]");
            Check("ITERATION12-FIX7-FIX20-CAP-NECK-PREVIEW-USES-GRID-AND-BELL-PROFILE-SCHEMATIC",
                packagingPreviewUsesEngineeringGridAndRoundedPcoProfile, ref pass, ref fail,
                "The cap/neck section is rendered on the same dark engineering grid and uses rounded, asymmetric crest/flank transitions instead of the previous angular tooth polygon.");
            bool packagingPreviewKeepsCharacteristicsTable =
                packagingPreviewSource.Contains("BuildPropertiesTable()") &&
                packagingPreviewSource.Contains("ХАРАКТЕРИСТИКИ РЕЗЬБЫ") &&
                packagingPreviewSource.Contains("_metricValues[keys[i]] = value") &&
                packagingPreviewSource.Contains("SetMetric(\"diameter\"") &&
                packagingPreviewSource.Contains("SetMetric(\"vents\"");
            Check("ITERATION12-FIX7-FIX20-CAP-NECK-PREVIEW-KEEPS-LIVE-CHARACTERISTICS-TABLE",
                packagingPreviewKeepsCharacteristicsTable, ref pass, ref fail,
                "Diameter, pitch, lead, height, clearance, starts, runout, vent-channel and chamfer values remain in the separate live characteristics table.");

            string[] iteration12ResourceKeys = {
                "NativeThreadConversionService.Stage.Preflight",
                "NativeThreadConversionService.Stage.ExistingOverlapCheck",
                "NativeThreadConversionService.Stage.WallThicknessCheck",
                "NativeThreadConversionService.Stage.CaptureEndOfPart",
                "NativeThreadConversionService.Stage.TransactionStart",
                "NativeThreadConversionService.Stage.RewindHistory",
                "NativeThreadConversionService.Stage.RemoveNativeThread",
                "NativeThreadConversionService.Stage.ResolveOriginalCylinder",
                "NativeThreadConversionService.Stage.BuildReplacement",
                "NativeThreadConversionService.Stage.RestoreHistoryAndRebuild",
                "NativeThreadConversionService.Stage.VerifyAndCommit",
                "NativeThreadConversionService.Error.PlanOrParametersMissing",
                "NativeThreadConversionService.Error.ConversionFailedAtStage",
                "NativeThreadConversionService.Error.UnexpectedFailureAtStage",
                "NativeThreadConversionService.Error.AbortFailed",
                "NativeThreadConversionService.Error.RollbackConfirmed",
                "NativeThreadConversionService.Error.RollbackUnverified",
                "NativeThreadConversionService.Error.NoMutationConfirmed",
                "NativeThreadConversionService.Error.OverlapContextUnavailable",
                "NativeThreadConversionService.Error.OverlapMetadataLoadFailed",
                "NativeThreadConversionService.Error.OverlapMetadataIncomplete",
                "NativeThreadConversionService.Error.OverlapGeometryMetadataIncomplete",
                "NativeThreadConversionService.Error.OverlapDetected",
                "NativeThreadConversionService.Error.OverlapIntervalUnknown",
                "NativeThreadConversionService.Error.HistoryTreeUnavailable",
                "NativeThreadConversionService.Error.HistoryPositionNotResolved",
                "NativeThreadConversionService.Error.EndOfPartBeforeNativeThread",
                "NativeThreadConversionService.Error.EndOfPartSnapshotFailed",
                "NativeThreadConversionService.Error.OriginalCylinderRecoveryFailed",
                "NativeThreadConversionService.Error.OriginalCylinderNotFound",
                "NativeThreadConversionService.Error.OriginalCylinderAmbiguous",
                "NativeThreadConversionService.Error.UndoContextUnavailable",
                "NativeThreadConversionService.Error.UndoLookupFailed",
                "NativeThreadConversionService.Error.UndoFeatureNotFound",
                "NativeThreadConversionService.Error.UndoFeatureAmbiguous",
                "NativeThreadConversionService.Error.UndoParametersFailed",
                "NativeThreadConversionService.Failure.ActiveDocumentMustBePart",
                "NativeThreadConversionService.Failure.ActiveDocumentChanged",
                "NativeThreadConversionService.Failure.SourceFeatureChanged",
                "NativeThreadConversionService.Failure.ExternalInternalMismatch",
                "NativeThreadConversionService.Failure.ThreadFamilyMismatch",
                "NativeThreadConversionService.Failure.TaperDesignationLocked",
                "NativeThreadConversionService.Failure.NominalDiameterLocked",
                "NativeThreadConversionService.Failure.InvalidReplacementParameters",
                "NativeThreadConversionService.Failure.ExistingOverlapRejected",
                "NativeThreadConversionService.Failure.TransactionCouldNotStart",
                "NativeThreadConversionService.Failure.HistoryRewindFailed",
                "NativeThreadConversionService.Failure.UpdateAfterNativeRemovalFailed",
                "NativeThreadConversionService.Failure.MetadataAttachmentFailed",
                "NativeThreadConversionService.Failure.DownstreamRebuildFailed",
                "NativeThreadConversionService.Failure.HistoryPositionRestoreFailed",
                "NativeThreadConversionService.Failure.MetadataVerificationFailed"
            };
            bool iteration12ResourcesValid = russianCatalog != null && englishCatalog != null &&
                iteration12ResourceKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                (int)HelivantaErrorCode.NativeThreadConversionFailed == 16 &&
                (int)HelivantaErrorCode.NativeThreadConversionRollbackUnverified == 17;
            string conversionFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "NativeThreadConversionService.Error.ConversionFailedAtStage", CultureInfo.InvariantCulture);
            string conversionFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "NativeThreadConversionService.Error.ConversionFailedAtStage", CultureInfo.InvariantCulture);
            bool conversionTemplateFormats = !string.IsNullOrWhiteSpace(conversionFailureRu) &&
                !string.IsNullOrWhiteSpace(conversionFailureEn) &&
                string.Format(CultureInfo.GetCultureInfo("ru-RU"), conversionFailureRu, "откат истории", "ошибка обновления").Contains("откат истории") &&
                string.Format(CultureInfo.GetCultureInfo("en-US"), conversionFailureEn, "rewinding history", "update failure").Contains("rewinding history");
            string[] fix6TaperedFailureKeys = {
                "NativeTaperedThreadBuilder.Error.PhysicalProfileCreationFailed",
                "NativeTaperedThreadBuilder.Error.PhysicalCoilCreationFailed"
            };
            bool fix6TaperedFailureResourcesValid = russianCatalog != null && englishCatalog != null &&
                fix6TaperedFailureKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                (int)HelivantaErrorCode.TaperedPhysicalProfileCreationFailed == 24 &&
                (int)HelivantaErrorCode.TaperedPhysicalCoilCreationFailed == 25;
            Check("ITERATION12-FIX7-FIX6-TAPERED-PROFILE-COIL-FAILURES-HAVE-RU-EN-RESOURCES-AND-STABLE-CODES",
                fix6TaperedFailureResourcesValid, ref pass, ref fail,
                "Typed profile/coil failure codes are append-only and both semantic error resources exist in RU/EN with English-only values.");

            string packagingVentSlotFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadGeometryBuilder.Error.VentSlotCreationFailed", CultureInfo.InvariantCulture);
            string packagingVentSlotFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadGeometryBuilder.Error.VentSlotCreationFailed", CultureInfo.InvariantCulture);
            bool packagingVentSlotFailureResourcesValid =
                !string.IsNullOrWhiteSpace(packagingVentSlotFailureRu) &&
                !string.IsNullOrWhiteSpace(packagingVentSlotFailureEn) &&
                packagingVentSlotFailureRu.Contains("Операция будет отменена") &&
                packagingVentSlotFailureEn.Contains("operation will be cancelled") &&
                !packagingVentSlotFailureEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.PackagingVentSlotCreationFailed == 28;
            Check("ITERATION12-FIX7-FIX11-PACKAGING-VENT-SLOT-FAILURE-HAS-RU-EN-RESOURCE-AND-STABLE-CODE",
                packagingVentSlotFailureResourcesValid, ref pass, ref fail,
                "Vent-slot construction failures use append-only code 28, a safe localized cancellation message, and keep raw COM details out of the UI.");

            string[] fix7Fix10TaperedStageFailureKeys = {
                "NativeTaperedThreadBuilder.Error.NativeFeatureCreationFailed",
                "NativeTaperedThreadBuilder.Error.PhysicalGrooveCreationFailed"
            };
            bool fix7Fix10TaperedStageFailureResourcesValid = russianCatalog != null && englishCatalog != null &&
                fix7Fix10TaperedStageFailureKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !(englishCatalog.GetString(key, CultureInfo.InvariantCulture) ?? string.Empty).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                (int)HelivantaErrorCode.TaperedNativeFeatureCreationFailed == 26 &&
                (int)HelivantaErrorCode.TaperedPhysicalGrooveCreationFailed == 27;
            Check("ITERATION12-FIX7-FIX10-TAPERED-NATIVE-AND-PHYSICAL-GROOVE-FAILURES-HAVE-RU-EN-RESOURCES-AND-STABLE-CODES",
                fix7Fix10TaperedStageFailureResourcesValid, ref pass, ref fail,
                "Native ThreadFeature and physical groove failures use append-only typed codes 26/27 and complete English-only RU/EN semantic resources.");


            Check("ITERATION12-TRANSACTION-CONVERSION-FAILURES-HAVE-RU-EN-RESOURCES-AND-STABLE-CODES",
                iteration12ResourcesValid && conversionTemplateFormats, ref pass, ref fail,
                "Conversion stages, known refusal causes, rollback-state messages and Undo recovery errors have complete embedded RU/EN resources; formatting works; new error codes are append-only (16, 17).");

            string metadataAttachmentFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadBuilder.Metadata.AttachmentFailed", CultureInfo.InvariantCulture);
            string metadataAttachmentFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadBuilder.Metadata.AttachmentFailed", CultureInfo.InvariantCulture);
            bool metadataAttachmentFailureResourceValid =
                !string.IsNullOrWhiteSpace(metadataAttachmentFailureRu) &&
                !string.IsNullOrWhiteSpace(metadataAttachmentFailureEn) &&
                metadataAttachmentFailureRu.Contains("Построение отменено") &&
                metadataAttachmentFailureEn.Contains("build was canceled") &&
                !metadataAttachmentFailureEn.Any(ch =>
                    (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.ThreadMetadataAttachmentFailed == 18;
            Check("ITERATION12-FIX3-METADATA-ATTACHMENT-FAILURE-IS-FAIL-CLOSED", metadataAttachmentFailureResourceValid,
                ref pass, ref fail,
                "Metadata attachment failure has a complete bilingual user message and append-only stable error code 18; geometry must be aborted instead of left uneditable.");

            string browserGroupingFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.BrowserGroupingFailed", CultureInfo.InvariantCulture);
            string browserGroupingFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.BrowserGroupingFailed", CultureInfo.InvariantCulture);
            bool browserGroupingMessageValid = !string.IsNullOrWhiteSpace(browserGroupingFailureRu) &&
                !string.IsNullOrWhiteSpace(browserGroupingFailureEn) &&
                browserGroupingFailureRu.Contains("автоматический подбор шага остановлен") &&
                browserGroupingFailureEn.Contains("automatic pitch retry was stopped") &&
                !browserGroupingFailureEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.PackagingThreadBrowserGroupingFailed == 20;
            bool retryClassificationCorrect =
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.PackagingThreadBrowserGroupingFailed, "browser mapping failed")) &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.ThreadMetadataAttachmentFailed, "metadata failed")) &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new InvalidOperationException("outer",
                    new HelivantaOperationException(HelivantaErrorCode.PackagingThreadRollbackUnverified, "rollback failed"))) &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.PackagingThreadSelectionRestoreFailed, "selection restore failed")) &&
                !PackagingAutoPitchFailurePolicy.ShouldStopRetry(new InvalidOperationException("candidate geometry rejected"));
            Check("PACKAGING-AUTOPITCH-INFRASTRUCTURE-FAILURES-STOP-RETRY-BILINGUALLY", 
                browserGroupingMessageValid && retryClassificationCorrect, ref pass, ref fail,
                "Browser mapping, metadata attachment, unverified rollback and unverified post-Abort edge restoration failures use stable codes and stop pitch retries; ordinary candidate rejection remains retryable; RU/EN warning is complete.");

            string overlapConflictRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.ExistingThreadOverlap", CultureInfo.InvariantCulture);
            string overlapConflictEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.ExistingThreadOverlap", CultureInfo.InvariantCulture);
            string overlapCheckRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.OverlapCheckFailed", CultureInfo.InvariantCulture);
            string overlapCheckEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.OverlapCheckFailed", CultureInfo.InvariantCulture);
            string requestedLengthRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.RequestedLengthExceedsAvailable", CultureInfo.InvariantCulture);
            string requestedLengthEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.RequestedLengthExceedsAvailable", CultureInfo.InvariantCulture);
            bool overlapResourcesValid = !string.IsNullOrWhiteSpace(overlapConflictRu) && overlapConflictRu.Contains("{0:0.###}") &&
                overlapConflictRu.Contains("{1:0.###}") && overlapConflictRu.Contains("{2:0.###}") &&
                overlapConflictRu.Contains("противоположного торца") && !string.IsNullOrWhiteSpace(overlapConflictEn) &&
                overlapConflictEn.Contains("{0:0.###}") && overlapConflictEn.Contains("{1:0.###}") &&
                overlapConflictEn.Contains("{2:0.###}") && overlapConflictEn.Contains("opposite cylinder end") &&
                !overlapConflictEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !string.IsNullOrWhiteSpace(overlapCheckRu) && !string.IsNullOrWhiteSpace(overlapCheckEn) &&
                !overlapCheckEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !string.IsNullOrWhiteSpace(requestedLengthRu) && requestedLengthRu.Contains("{0:0.###}") && requestedLengthRu.Contains("{1:0.###}") &&
                !string.IsNullOrWhiteSpace(requestedLengthEn) && requestedLengthEn.Contains("{0:0.###}") && requestedLengthEn.Contains("{1:0.###}") &&
                !requestedLengthEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.PackagingThreadOverlapDetected == 21 &&
                (int)HelivantaErrorCode.PackagingThreadOverlapCheckFailed == 22 &&
                (int)HelivantaErrorCode.PackagingThreadLengthExceedsAvailable == 23 &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.PackagingThreadOverlapDetected, "overlap")) &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.PackagingThreadOverlapCheckFailed, "unverifiable overlap")) &&
                PackagingAutoPitchFailurePolicy.ShouldStopRetry(new HelivantaOperationException(
                    HelivantaErrorCode.PackagingThreadLengthExceedsAvailable, "length exceeds available"));
            Check("PACKAGING-OVERLAP-GUARD-USES-APPENDED-CODES-AND-COMPLETE-RU-EN-ACTIONS",
                overlapResourcesValid, ref pass, ref fail,
                "New-build overlap, unverifiable-metadata, and insufficient-length guards have append-only codes 21/22/23, actionable complete bilingual messages, compatible numeric placeholders, and stop automatic pitch retries.");

            string[] editToCreatePromptKeys = {
                "Legacy_4bd7ec7be92b1add",
                "Legacy_065cd244267ec870",
                "Legacy_24f6673702926948",
                "Legacy_aeea82df1c288d2a"
            };
            bool editToCreatePromptResourcesValid = russianCatalog != null && englishCatalog != null &&
                editToCreatePromptKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture))) &&
                russianCatalog.GetString(editToCreatePromptKeys[0], CultureInfo.InvariantCulture).Length <= 32 &&
                editToCreatePromptKeys.Skip(1).All(key =>
                    russianCatalog.GetString(key, CultureInfo.InvariantCulture).Length <= 160 &&
                    englishCatalog.GetString(key, CultureInfo.InvariantCulture).Length <= 160 &&
                    !englishCatalog.GetString(key, CultureInfo.InvariantCulture).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')));
            Check("EDIT-TO-CREATE-CONFIRMATION-PROMPTS-FIT-COMPACT-RU-EN-DIALOG", editToCreatePromptResourcesValid,
                ref pass, ref fail,
                "The transition title and all three mode-specific confirmation prompts must have complete RU/EN values with bounded lengths compatible with measured compact layout.");

            string autoPitchCadRejectedRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.AutoPitch.Error.CadRejectedAllCandidates", CultureInfo.InvariantCulture);
            string autoPitchCadRejectedEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.AutoPitch.Error.CadRejectedAllCandidates", CultureInfo.InvariantCulture);
            string autoPitchFailureSummaryRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.AutoPitch.BuildFailureSummary", CultureInfo.InvariantCulture);
            string autoPitchFailureSummaryEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.AutoPitch.BuildFailureSummary", CultureInfo.InvariantCulture);
            bool autoPitchFailureUiSafe = !string.IsNullOrWhiteSpace(autoPitchCadRejectedRu) &&
                !string.IsNullOrWhiteSpace(autoPitchCadRejectedEn) &&
                !string.IsNullOrWhiteSpace(autoPitchFailureSummaryRu) &&
                !string.IsNullOrWhiteSpace(autoPitchFailureSummaryEn) &&
                autoPitchCadRejectedRu.Contains("журнал диагностики") &&
                autoPitchCadRejectedEn.Contains("diagnostic log") &&
                !autoPitchCadRejectedEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                autoPitchFailureSummaryRu.Contains("{0}") && autoPitchFailureSummaryEn.Contains("{0}");
            Check("AUTO-PITCH-CAD-FAILURE-SUMMARY-USES-STABLE-RU-EN-REASON", autoPitchFailureUiSafe,
                ref pass, ref fail,
                "Failed Inventor candidate exception text must stay in diagnostics; the summary and nested reason must come from complete bilingual resources.");

            string runoutLoftFallbackRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadBuilder.Runout.LoftFallbackWarning", CultureInfo.InvariantCulture);
            string runoutLoftFallbackEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadBuilder.Runout.LoftFallbackWarning", CultureInfo.InvariantCulture);
            bool runoutLoftFallbackSafe = !string.IsNullOrWhiteSpace(runoutLoftFallbackRu) &&
                !string.IsNullOrWhiteSpace(runoutLoftFallbackEn) &&
                runoutLoftFallbackRu.Contains("журнал диагностики") &&
                runoutLoftFallbackEn.Contains("diagnostic log") &&
                !runoutLoftFallbackEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("RUNOUT-LOFT-FALLBACK-WARNING-IS-LOCALIZED-AND-LOGS-RAW-EXCEPTION", runoutLoftFallbackSafe,
                ref pass, ref fail,
                "A failed Loft must keep the successful segmented fallback, show a complete RU/EN warning, and keep the raw Inventor exception in diagnostics only.");

            object cleanupA = new object();
            object cleanupB = new object();
            object cleanupC = new object();
            var cleanupAttemptOrder = new List<object>();
            string cleanupFailureDetail;
            bool failedCleanupAccepted = RunoutTemporaryCleanupPolicy.TryDeleteAll(
                new[] { cleanupA, cleanupB, cleanupC },
                item =>
                {
                    cleanupAttemptOrder.Add(item);
                    if (ReferenceEquals(item, cleanupB))
                        throw new InvalidOperationException("Injected temporary-object Delete failure.");
                }, out cleanupFailureDetail);
            bool failedCleanupRejectedFallback = !failedCleanupAccepted &&
                cleanupAttemptOrder.SequenceEqual(new[] { cleanupC, cleanupB, cleanupA }) &&
                !string.IsNullOrWhiteSpace(cleanupFailureDetail) &&
                cleanupFailureDetail.Contains("cleanupFailureCount=1", StringComparison.Ordinal);
            Check("RUNOUT-LOFT-CLEANUP-FAILURE-REJECTS-SEGMENTED-FALLBACK", failedCleanupRejectedFallback,
                ref pass, ref fail,
                "A failed temporary Loft-object Delete is reported, remaining cleanup is still attempted in reverse dependency order, and the caller is not permitted to add a segmented fallback.");

            var successfulCleanupOrder = new List<object>();
            bool successfulCleanupAllowsFallback = RunoutTemporaryCleanupPolicy.TryDeleteAll(
                new[] { cleanupA, cleanupB, cleanupC }, item => successfulCleanupOrder.Add(item),
                out string successfulCleanupDetail);
            Check("RUNOUT-LOFT-CLEANUP-SUCCESS-PRESERVES-SEGMENTED-FALLBACK", successfulCleanupAllowsFallback &&
                successfulCleanupOrder.SequenceEqual(new[] { cleanupC, cleanupB, cleanupA }) &&
                string.IsNullOrEmpty(successfulCleanupDetail), ref pass, ref fail,
                "The existing segmented fallback remains available after every temporary Loft object was successfully deleted.");


            string[] fix5InlineKeys = {
                "PackagingThreadDialog.Error.InlineFailure",
                "PackagingThreadDialog.Error.ReasonUnavailable",
                "PackagingThreadDialog.Build.CancelledSummaryWithReason",
                "PackagingThreadDialog.Build.CancelRequestedWithReason",
                "PackagingThreadDialog.Edit.CancelledRestoredWithReason",
                "PackagingThreadDialog.Edit.NewThreadButton",
                "PackagingThreadDialog.Edit.NewThreadTooltip",
                "PackagingThreadDialog.Edit.NewThreadStarted",
                "PackagingThreadDialog.Edit.NewThreadSelectionClearFailed",
                "PackagingThreadDialog.Cancel.Reason.WindowClose",
                "PackagingThreadDialog.Cancel.Reason.MainDialogCancel",
                "PackagingThreadDialog.Cancel.Reason.MainDialogClose",
                "PackagingThreadDialog.Cancel.Reason.User",
                "PackagingThreadDialog.Cancel.Reason.CloseButton",
                "PackagingThreadDialog.Cancel.Reason.External",
                "ThreadDialog.Edit.BuildZonesLoadFailed",
                "ThreadEditService.Error.ReplacementFailed.BeforeTransactionWithCause",
                "ThreadEditService.Error.ReplacementFailed.RollbackVerifiedWithCause",
                "ThreadEditService.Error.ReplacementFailed.RollbackUnverifiedWithCause"
            };
            bool fix5InlineResourcesValid = russianCatalog != null && englishCatalog != null &&
                fix5InlineKeys.All(key =>
                    !string.IsNullOrWhiteSpace(russianCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !string.IsNullOrWhiteSpace(englishCatalog.GetString(key, CultureInfo.InvariantCulture)) &&
                    !englishCatalog.GetString(key, CultureInfo.InvariantCulture).Any(ch =>
                        (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))) &&
                russianCatalog.GetString("PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture).Contains("{0}") &&
                russianCatalog.GetString("PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture).Contains("{1}") &&
                englishCatalog.GetString("PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture).Contains("{1}") &&
                russianCatalog.GetString("PackagingThreadDialog.Build.CancelledSummaryWithReason", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("PackagingThreadDialog.Build.CancelledSummaryWithReason", CultureInfo.InvariantCulture).Contains("{0}") &&
                russianCatalog.GetString("PackagingThreadDialog.Edit.CancelledRestoredWithReason", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("PackagingThreadDialog.Edit.CancelledRestoredWithReason", CultureInfo.InvariantCulture).Contains("{0}") &&
                russianCatalog.GetString("ThreadEditService.Error.ReplacementFailed.BeforeTransactionWithCause", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("ThreadEditService.Error.ReplacementFailed.BeforeTransactionWithCause", CultureInfo.InvariantCulture).Contains("{0}") &&
                russianCatalog.GetString("ThreadEditService.Error.ReplacementFailed.RollbackVerifiedWithCause", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("ThreadEditService.Error.ReplacementFailed.RollbackVerifiedWithCause", CultureInfo.InvariantCulture).Contains("{0}") &&
                russianCatalog.GetString("ThreadEditService.Error.ReplacementFailed.RollbackUnverifiedWithCause", CultureInfo.InvariantCulture).Contains("{0}") &&
                englishCatalog.GetString("ThreadEditService.Error.ReplacementFailed.RollbackUnverifiedWithCause", CultureInfo.InvariantCulture).Contains("{0}");
            Check("ITERATION12-FIX5-PACKAGING-INLINE-ERROR-AND-CANCELLATION-RESOURCES-HAVE-RU-EN-PARITY",
                fix5InlineResourcesValid, ref pass, ref fail,
                "Cap/Neck inline error and cancellation cause messages must exist in both embedded catalogs, have complete formatting arguments, and contain no Cyrillic in English values.");

            string downstreamTitleRu = russianCatalog == null ? null : russianCatalog.GetString("ThreadDialog.Edit.DownstreamRiskTitle", CultureInfo.InvariantCulture);
            string downstreamTitleEn = englishCatalog == null ? null : englishCatalog.GetString("ThreadDialog.Edit.DownstreamRiskTitle", CultureInfo.InvariantCulture);
            string downstreamMessageRu = russianCatalog == null ? null : russianCatalog.GetString("ThreadDialog.Edit.DownstreamRiskMessage", CultureInfo.InvariantCulture);
            string downstreamMessageEn = englishCatalog == null ? null : englishCatalog.GetString("ThreadDialog.Edit.DownstreamRiskMessage", CultureInfo.InvariantCulture);
            bool downstreamPromptValid = !string.IsNullOrWhiteSpace(downstreamTitleRu) &&
                !string.IsNullOrWhiteSpace(downstreamTitleEn) &&
                !string.IsNullOrWhiteSpace(downstreamMessageRu) && downstreamMessageRu.Contains("{0}") &&
                !string.IsNullOrWhiteSpace(downstreamMessageEn) && downstreamMessageEn.Contains("{0}") &&
                !downstreamMessageEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("ITERATION12-FIX5-DOWNSTREAM-HISTORY-WARNING-HAS-COMPLETE-RU-EN-PROMPT", downstreamPromptValid,
                ref pass, ref fail,
                "The compact downstream-history confirmation must use complete RU/EN resource text with the operation-count placeholder intact.");

            string packagingRollbackWarningRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.BuildRollbackUnverified", CultureInfo.InvariantCulture);
            string packagingRollbackWarningEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.BuildRollbackUnverified", CultureInfo.InvariantCulture);
            bool packagingRollbackWarningValid = !string.IsNullOrWhiteSpace(packagingRollbackWarningRu) &&
                !string.IsNullOrWhiteSpace(packagingRollbackWarningEn) &&
                packagingRollbackWarningRu.Contains("НЕ СОХРАНЯЙТЕ") &&
                packagingRollbackWarningEn.Contains("DO NOT SAVE") &&
                !packagingRollbackWarningEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.PackagingThreadRollbackUnverified == 19;
            Check("ITERATION12-FIX5-PACKAGING-ROLLBACK-FAILURE-HAS-APPENDED-CODE-AND-CRITICAL-RU-EN-WARNING",
                packagingRollbackWarningValid, ref pass, ref fail,
                "If Abort throws during a Cap/Neck build, the inline UI must show a localized DO NOT SAVE warning and the stable error code must not renumber existing codes.");

            string autoPitchSelectionRestoreRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.AutoPitchSelectionRestoreFailed", CultureInfo.InvariantCulture);
            string autoPitchSelectionRestoreEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.AutoPitchSelectionRestoreFailed", CultureInfo.InvariantCulture);
            bool autoPitchSelectionRestoreResourcesValid =
                !string.IsNullOrWhiteSpace(autoPitchSelectionRestoreRu) && autoPitchSelectionRestoreRu.Contains("Автоматический подбор остановлен") &&
                !string.IsNullOrWhiteSpace(autoPitchSelectionRestoreEn) && autoPitchSelectionRestoreEn.Contains("Automatic pitch search was stopped") &&
                !autoPitchSelectionRestoreEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                (int)HelivantaErrorCode.PackagingThreadSelectionRestoreFailed == 38;
            Check("2.7.200 CAP-NECK-AUTO-PITCH-EDGE-RESTORE-FAILURE-HAS-APPENDED-CODE-AND-RU-EN-GUIDANCE",
                autoPitchSelectionRestoreResourcesValid, ref pass, ref fail,
                "An edge selection that cannot be restored and identity-verified after a failed auto-pitch candidate must stop retries with stable code 38 and complete RU/EN guidance.");

            string localizationDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaLocalization.cs"), Encoding.UTF8)
                : string.Empty;
            string threadDialogDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            string packagingDialogDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            string threadAddInDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadProAddIn.cs"), Encoding.UTF8)
                : string.Empty;
            string ribbonLifecycleDiagnosticsSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "RibbonLifecycleDiagnostics.cs"), Encoding.UTF8)
                : string.Empty;
            string ribbonCommandIdentityPolicySource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "RibbonCommandIdentityPolicy.cs"), Encoding.UTF8)
                : string.Empty;
            string nativeThreadConversionDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeThreadConversionService.cs"), Encoding.UTF8)
                : string.Empty;
            string nativeTaperedThreadBuilderDiagnosticSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "NativeTaperedThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            int loadEditParametersStart = threadDialogDiagnosticSource.IndexOf(
                "private void LoadEditParameters(ThreadParametersSnapshot p", StringComparison.Ordinal);
            int loadEditParametersEnd = loadEditParametersStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf(
                "private static decimal ClampDecimal(", loadEditParametersStart, StringComparison.Ordinal);
            string loadEditParametersSource = loadEditParametersStart >= 0 && loadEditParametersEnd > loadEditParametersStart
                ? threadDialogDiagnosticSource.Substring(loadEditParametersStart, loadEditParametersEnd - loadEditParametersStart)
                : string.Empty;
            int nativeConversionStart = threadDialogDiagnosticSource.IndexOf(
                "internal bool TryBeginNativeConversion(", StringComparison.Ordinal);
            int nativeConversionEnd = nativeConversionStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf(
                "private void DisableActionsAfterParameterLoadFailure(", nativeConversionStart, StringComparison.Ordinal);
            string nativeConversionRecoverySource = nativeConversionStart >= 0 && nativeConversionEnd > nativeConversionStart
                ? threadDialogDiagnosticSource.Substring(nativeConversionStart, nativeConversionEnd - nativeConversionStart)
                : string.Empty;
            string parameterLoadFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.Status.ParameterLoadFailed", CultureInfo.InvariantCulture);
            string parameterLoadFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.Status.ParameterLoadFailed", CultureInfo.InvariantCulture);
            bool parameterLoadingStateIsAlwaysRestored =
                loadEditParametersSource.Contains("bool wasLoadingAtEntry = _loading;") &&
                loadEditParametersSource.Contains("try\n") &&
                loadEditParametersSource.Contains("finally") &&
                loadEditParametersSource.Contains("_loading = wasLoadingAtEntry;") &&
                loadEditParametersSource.LastIndexOf("finally", StringComparison.Ordinal) >
                    loadEditParametersSource.LastIndexOf("UpdateInfo();", StringComparison.Ordinal);
            Check("THREAD-DIALOG-LOAD-EDIT-PARAMETERS-ALWAYS-RESTORES-LOADING-FLAG",
                parameterLoadingStateIsAlwaysRestored, ref pass, ref fail,
                "LoadEditParameters restores the caller's prior _loading state in finally, including exceptions between its staged UI refreshes.");
            bool nativeConversionParameterRecoveryFailsClosed =
                nativeConversionRecoverySource.Contains("previousPlanRestored") &&
                nativeConversionRecoverySource.Contains("catch (Exception restoreException)") &&
                nativeConversionRecoverySource.Contains("DisableActionsAfterParameterLoadFailure(error);") &&
                !nativeConversionRecoverySource.Contains("try { if (previousPlan != null) LoadEditParameters(previousPlan.Parameters); } catch { }") &&
                threadDialogDiagnosticSource.Contains("if (bBuild != null) bBuild.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("if (bReset != null) bReset.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("if (bUndo != null) bUndo.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("if (bPreview != null) { bPreview.Enabled = false; bPreview.Visible = false; }") &&
                threadDialogDiagnosticSource.Contains("if (bCreateNew != null) bCreateNew.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("if (bEditExisting != null) bEditExisting.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("if (bResume != null) bResume.Enabled = false;");
            Check("THREAD-DIALOG-NATIVE-CONVERSION-RESTORE-FAILURE-DISABLES-MUTATION-ACTIONS",
                nativeConversionParameterRecoveryFailsClosed, ref pass, ref fail,
                "A failed previous-plan parameter restore is diagnosed and leaves build/edit/preview/undo actions disabled instead of silently re-enabling Next.");
            Check("THREAD-DIALOG-PARAMETER-RESTORE-FAILURE-STATUS-HAS-RU-EN-RESOURCES",
                !string.IsNullOrWhiteSpace(parameterLoadFailureRu) &&
                !string.IsNullOrWhiteSpace(parameterLoadFailureEn) &&
                !parameterLoadFailureEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')),
                ref pass, ref fail,
                "The fail-closed parameter-restore status is present in both RU/EN resources and English contains no Cyrillic leakage.");
            string runtimeAuditAutomationSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8)
                : string.Empty;
            bool runtimeAuditCylinderExtrudeHasExplicitOperationAndSolidBodyCheck =
                runtimeAuditAutomationSource.Contains("profile, lengthCm, PartFeatureExtentDirectionEnum.kPositiveExtentDirection,") &&
                runtimeAuditAutomationSource.Contains("PartFeatureOperationEnum.kNewBodyOperation);") &&
                runtimeAuditAutomationSource.Contains("surfaceBodyCount == 1") &&
                runtimeAuditAutomationSource.Contains("createdBody.IsSolid");
            Check("RUNTIME-AUDIT-CYLINDER-EXTRUDE-SUPPLIES-EXPLICIT-NEW-BODY-OPERATION",
                runtimeAuditCylinderExtrudeHasExplicitOperationAndSolidBodyCheck, ref pass, ref fail,
                "The runtime audit's first-feature extrusion must supply Inventor's required Operation argument, verify exactly one body, and require that body to be solid.");
            bool runtimeAuditCapturesCommittedHistoryAroundAbort =
                runtimeAuditAutomationSource.Contains("DescribeCommittedTransactionHistory(_app)") &&
                runtimeAuditAutomationSource.Contains("CaptureCommittedTransactionSnapshot(_app)") &&
                runtimeAuditAutomationSource.Contains("CanUndoBeforeRejectedEdit") &&
                runtimeAuditAutomationSource.Contains("CanUndoAfterAbort") &&
                runtimeAuditAutomationSource.Contains("HasNewCommittedHelivantaTransaction") &&
                runtimeAuditAutomationSource.Contains("LastCommittedTransactionIsHelivanta") &&
                runtimeAuditAutomationSource.Contains("CommittedTransactionsBeforeRejectedEdit") &&
                runtimeAuditAutomationSource.Contains("CommittedTransactionsAfterAbort") &&
                runtimeAuditAutomationSource.Contains("TransactionManager.CurrentTransaction") &&
                !runtimeAuditAutomationSource.Contains("Require(canUndoAfterAbort,");
            Check("RUNTIME-AUDIT-ABORT-DOES-NOT-REQUIRE-UNDO-OF-OLDER-BUILD",
                runtimeAuditCapturesCommittedHistoryAroundAbort, ref pass, ref fail,
                "The audit must verify rollback and absence of a newly committed Helivanta transaction after Abort, while keeping Undo fail-closed when an ordinary Inventor transaction is on top.");
            int standardExternalStart = runtimeAuditAutomationSource.IndexOf("private void RunStandardExternalCase", StringComparison.Ordinal);
            int standardExternalEnd = standardExternalStart < 0 ? -1 : runtimeAuditAutomationSource.IndexOf("private void RunStandardEditRollbackChecks", standardExternalStart, StringComparison.Ordinal);
            string standardExternalAuditSource = standardExternalStart >= 0 && standardExternalEnd > standardExternalStart
                ? runtimeAuditAutomationSource.Substring(standardExternalStart, standardExternalEnd - standardExternalStart)
                : string.Empty;
            int buildCallIndex = standardExternalAuditSource.IndexOf("ThreadBuilder.BuildAll", StringComparison.Ordinal);
            int immediateUndoCheckIndex = standardExternalAuditSource.IndexOf("CanUndoImmediatelyAfterBuild", StringComparison.Ordinal);
            int verifyAfterBuildIndex = standardExternalAuditSource.IndexOf("VerifyPartAndMetadata", StringComparison.Ordinal);
            bool runtimeAuditChecksUndoImmediatelyAfterBuild = buildCallIndex >= 0 && immediateUndoCheckIndex > buildCallIndex &&
                verifyAfterBuildIndex > immediateUndoCheckIndex && standardExternalAuditSource.Contains("LastCommittedTransactionIsHelivanta");
            Check("RUNTIME-AUDIT-CHECKS-UNDO-IMMEDIATELY-AFTER-BUILD",
                runtimeAuditChecksUndoImmediatelyAfterBuild, ref pass, ref fail,
                "The real Undo availability check must run directly after BuildAll, before the audit performs Update2/metadata/browser inspections that create unrelated Inventor transactions.");
            bool runtimeAuditUndoRemainsFailClosedAfterUndo =
                runtimeAuditAutomationSource.Contains("CanUndoAfterUndo") &&
                runtimeAuditAutomationSource.Contains("LastCommittedTransactionAfterUndoIsHelivanta") &&
                runtimeAuditAutomationSource.Contains("Require(canUndoAfterUndo == lastTransactionAfterUndoIsHelivanta");
            Check("RUNTIME-AUDIT-UNDO-AFTER-UNDO-RESPECTS-TRANSACTION-OWNER",
                runtimeAuditUndoRemainsFailClosedAfterUndo, ref pass, ref fail,
                "After Undo removes the edit transaction, the audit must not skip a newer unrelated Inventor transaction to expose an older build.");
            bool runtimeAuditCapNeckUsesPersistedMetadataThreadId =
                runtimeAuditAutomationSource.Contains("string buildSummary = PackagingThreadGeometryBuilder.Build(_app, edge, resolved") &&
                runtimeAuditAutomationSource.Contains("string threadId = original.Record.ThreadId;") &&
                runtimeAuditAutomationSource.Contains("original.Record.PackagingParameters != null") &&
                runtimeAuditAutomationSource.Contains("LengthInputValue = 26.0") &&
                runtimeAuditAutomationSource.Contains("TryReplacePackaging(_app, threadId, replacementResolved,\n                    26.0, replacement") &&
                runtimeAuditAutomationSource.Contains("PackagingParameters.LengthInputValue - 26.0") &&
                !runtimeAuditAutomationSource.Contains("string threadId = PackagingThreadGeometryBuilder.Build(");
            Check("RUNTIME-AUDIT-CAPNECK-USES-PERSISTED-METADATA-THREADID-NOT-BUILD-SUMMARY",
                runtimeAuditCapNeckUsesPersistedMetadataThreadId, ref pass, ref fail,
                "PackagingThreadGeometryBuilder.Build returns a user-facing summary; the audit must obtain the authoritative ThreadId from the persisted Cap/Neck metadata group.");

            string packagingPostCommitDialogSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            string packagingPostCommitProbeSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingPostCommitRuntimeAuditProbe.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingPostCommitRuntimeAuditProbe.cs"), Encoding.UTF8)
                : string.Empty;
            int packagingValidateStart = packagingPostCommitDialogSource.IndexOf("private void ValidateParameters(bool buildGeometry)", StringComparison.Ordinal);
            int packagingValidateEnd = packagingValidateStart < 0 ? -1 : packagingPostCommitDialogSource.IndexOf("private void ReportCommittedBuildUiFinalizationFailure(", packagingValidateStart, StringComparison.Ordinal);
            string packagingValidateMethod = packagingValidateStart >= 0 && packagingValidateEnd > packagingValidateStart
                ? packagingPostCommitDialogSource.Substring(packagingValidateStart, packagingValidateEnd - packagingValidateStart)
                : string.Empty;
            int packagingCommittedCatchStart = packagingValidateMethod.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
            int packagingCommittedCatchEnd = packagingCommittedCatchStart < 0 ? -1 : packagingValidateMethod.IndexOf("finally", packagingCommittedCatchStart, StringComparison.Ordinal);
            string packagingCommittedCatch = packagingCommittedCatchStart >= 0 && packagingCommittedCatchEnd > packagingCommittedCatchStart
                ? packagingValidateMethod.Substring(packagingCommittedCatchStart, packagingCommittedCatchEnd - packagingCommittedCatchStart)
                : string.Empty;
            int packagingCommittedBranchStart = packagingCommittedCatch.IndexOf("if (geometryCommitted)", StringComparison.Ordinal);
            int packagingCommittedBranchEnd = packagingCommittedBranchStart < 0 ? -1 : packagingCommittedCatch.IndexOf("else", packagingCommittedBranchStart, StringComparison.Ordinal);
            string packagingCommittedBranch = packagingCommittedBranchStart >= 0 && packagingCommittedBranchEnd > packagingCommittedBranchStart
                ? packagingCommittedCatch.Substring(packagingCommittedBranchStart, packagingCommittedBranchEnd - packagingCommittedBranchStart)
                : string.Empty;
            bool packagingDialogPreservesCommittedBuildAfterUiFailure =
                packagingPostCommitDialogSource.Contains("bool geometryCommitted = false;") &&
                packagingPostCommitDialogSource.Contains("geometryCommitted = buildGeometry;") &&
                packagingPostCommitDialogSource.Contains("autoPitchBuildSucceeded = geometryCommitted;") &&
                packagingCommittedBranch.Contains("ReportCommittedBuildUiFinalizationFailure(ex, diagnosticStage, diagnosticContext);") &&
                !packagingCommittedBranch.Contains("TryRestoreSelectedEdgeAfterUncommittedFailure") &&
                packagingPostCommitDialogSource.Contains("PackagingPostCommitRuntimeAuditProbe.RecordCommittedFailureHandled") &&
                packagingPostCommitProbeSource.Contains("RuntimeAuditAccessGate.IsEnabled()") &&
                packagingPostCommitProbeSource.Contains("_failureInjected = true");
            Check("CAPNECK-DIALOG-POSTCOMMIT-UI-FAILURE-DOES-NOT-RESTORE-STALE-EDGE-OR-REPORT-BUILD-FAILURE",
                packagingDialogPreservesCommittedBuildAfterUiFailure, ref pass, ref fail,
                "Once the real builder has returned after Transaction.End, later UI/reporting exceptions must preserve the committed-build state, avoid stale-edge Abort restoration, and show a dedicated localized warning. The one-shot injection is opt-in and exact-document gated.");
            bool packagingPostCommitAuditIsLive = runtimeAuditAutomationSource.Contains("CAPNECK-POSTCOMMIT-UI-FAILURE-KEEPS-COMMITTED-GEOMETRY") &&
                runtimeAuditAutomationSource.Contains("RunCapNeckPostCommitUiFailureCase(root, runId, result)") &&
                runtimeAuditAutomationSource.Contains("RunRuntimeAuditPostCommitUiFailureBuild(4.0, 24.0") &&
                runtimeAuditAutomationSource.Contains("probe.FailureHandledAsCommitted") &&
                runtimeAuditAutomationSource.Contains("probe.SelectionRestoreAttempted") &&
                runtimeAuditAutomationSource.Contains("LoadOnlyMetadataGroup(doc, true)") &&
                runtimeAuditAutomationSource.Contains("VerifyPartAndMetadata(doc, true, out ThreadMetadataGroup group, out ModelMetrics afterMetrics)") &&
                packagingPostCommitProbeSource.Contains("RecordCommittedFailureHandled") &&
                packagingPostCommitProbeSource.Contains("ShouldInjectAfterCommit");
            Check("RUNTIME-AUDIT-CAPNECK-POSTCOMMIT-UI-FAILURE-VERIFIES-REAL-COMMIT-AND-METADATA",
                packagingPostCommitAuditIsLive, ref pass, ref fail,
                "Inventor Runtime Audit must execute the real Cap/Neck dialog and builder on a disposable IPT, inject one exception only after the transaction commits, then verify the committed transaction, actual B-rep, persisted ThreadId/parameters and that stale Edge restoration was skipped.");
            string postCommitWarningRu = russianCatalog == null ? null : russianCatalog.GetString("PackagingThreadDialog.Build.PostCommitUiFailure", CultureInfo.InvariantCulture);
            string postCommitWarningEn = englishCatalog == null ? null : englishCatalog.GetString("PackagingThreadDialog.Build.PostCommitUiFailure", CultureInfo.InvariantCulture);
            Check("CAPNECK-POSTCOMMIT-UI-FAILURE-WARNING-HAS-RU-EN-RESOURCE-PARITY",
                !string.IsNullOrWhiteSpace(postCommitWarningRu) && !string.IsNullOrWhiteSpace(postCommitWarningEn) &&
                postCommitWarningRu.Contains("{0}") && postCommitWarningEn.Contains("{0}") &&
                !HelivantaLocalization.ContainsCyrillic(postCommitWarningEn), ref pass, ref fail,
                "The committed-geometry warning is a complete parameterized RU/EN resource and English contains no Cyrillic.");

            string editRecoveryProbeSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "ThreadDialogEditRecoveryRuntimeAuditProbe.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialogEditRecoveryRuntimeAuditProbe.cs"), Encoding.UTF8)
                : string.Empty;
            bool threadDialogPartialEditFailureFailsClosed =
                threadDialogDiagnosticSource.Contains("_editApplyBlocked = true;") &&
                threadDialogDiagnosticSource.Contains("if (_editApplyBlocked)") &&
                threadDialogDiagnosticSource.Contains("RecordApplyAttemptBlocked") &&
                threadDialogDiagnosticSource.Contains("RecordUndoAttemptBlocked") &&
                threadDialogDiagnosticSource.Contains("RecordPreviewAttemptBlocked") &&
                threadDialogDiagnosticSource.Contains("bBuild.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("bUndo.Enabled = false;") &&
                threadDialogDiagnosticSource.Contains("bPreview.Enabled = false;");
            Check("THREAD-DIALOG-PARTIAL-EDIT-LOAD-FAILURE-FAILS-CLOSED",
                threadDialogPartialEditFailureFailsClosed, ref pass, ref fail,
                "If the new group's parameters partially load but the previous snapshot cannot be restored, the previous ThreadId stays armed only as an identity marker and Apply/Undo/Preview handlers reject commands.");
            bool threadDialogEditRecoveryAuditIsLive =
                runtimeAuditAutomationSource.Contains("THREAD-DIALOG-PARTIAL-EDIT-LOAD-FAILURE-BLOCKS-APPLY") &&
                runtimeAuditAutomationSource.Contains("RunThreadDialogPartialEditLoadFailureCase(root, runId, result)") &&
                runtimeAuditAutomationSource.Contains("dialog.TryBeginEditByThreadId(firstThreadId") &&
                runtimeAuditAutomationSource.Contains("dialog.RunRuntimeAuditFailedEditSwitch(secondGroup)") &&
                runtimeAuditAutomationSource.Contains("recovery.ApplyAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("recovery.UndoAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("recovery.PreviewAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("recovery.ResetRecoverySucceeded") &&
                runtimeAuditAutomationSource.Contains("beforeMetrics.VolumeCm3 - afterMetrics.VolumeCm3") &&
                editRecoveryProbeSource.Contains("RuntimeAuditAccessGate.IsEnabled()") &&
                editRecoveryProbeSource.Contains("ReferenceEquals(_targetDocument, document)");
            Check("RUNTIME-AUDIT-THREAD-DIALOG-PARTIAL-EDIT-LOAD-FAILURE-VERIFIES-BLOCK-AND-RESET",
                threadDialogEditRecoveryAuditIsLive, ref pass, ref fail,
                "A new live Inventor case injects a controlled partial UI load after two real GOST groups exist, invokes production Apply/Undo/Preview handlers, proves B-rep/metadata preservation, then exercises the actual Reset recovery path.");
            string editRecoveryMessageRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.Edit.ParameterLoadRecoveryBlocked", CultureInfo.InvariantCulture);
            string editRecoveryMessageEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.Edit.ParameterLoadRecoveryBlocked", CultureInfo.InvariantCulture);
            bool crossFamilyEditSwitchAuditIsLive =
                runtimeAuditAutomationSource.Contains("THREAD-DIALOG-CROSS-FAMILY-STANDARD-TO-CAPNECK-FAIL-CLOSED") &&
                runtimeAuditAutomationSource.Contains("THREAD-DIALOG-CROSS-FAMILY-CAPNECK-TO-STANDARD-FAIL-CLOSED") &&
                runtimeAuditAutomationSource.Contains("RunCrossFamilyEditSwitchCase(root, runId, true, result)") &&
                runtimeAuditAutomationSource.Contains("RunCrossFamilyEditSwitchCase(root, runId, false, result)") &&
                runtimeAuditAutomationSource.Contains("ThreadBuilder.BuildAll(_app") &&
                runtimeAuditAutomationSource.Contains("PackagingThreadGeometryBuilder.Build(_app, packagingEdge") &&
                runtimeAuditAutomationSource.Contains("RunRuntimeAuditCrossFamilyBlockedAttempts()") &&
                runtimeAuditAutomationSource.Contains("blocked.PackagingBuildAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("blocked.ApplyAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("dialog.TryBeginEditByThreadId(targetId, out string successfulTransitionError)") &&
                runtimeAuditAutomationSource.Contains("IsPackagingEditRecoveryBlockClearedForRuntimeAudit") &&
                runtimeAuditAutomationSource.Contains("A fresh reload of the original metadata group") &&
                threadDialogDiagnosticSource.Contains("BlockEditSwitchAfterUnrecoverableRestore") &&
                threadDialogDiagnosticSource.Contains("BlockEditRecoveryAfterUnrecoverableSwitch") &&
                threadDialogDiagnosticSource.Contains("if (_editApplyBlocked && !_editMode)") &&
                runtimeAuditAutomationSource.Contains("ResetDialogDefaults();");
            Check("RUNTIME-AUDIT-THREAD-DIALOG-CROSS-FAMILY-TRANSITIONS-FAIL-CLOSED",
                crossFamilyEditSwitchAuditIsLive, ref pass, ref fail,
                "Two separate live Inventor cases exercise Standard-to-Cap/Neck and Cap/Neck-to-Standard with real geometry, injected partial UI-load faults, direct outer/embedded command calls, explicit full reload recovery, and B-rep preservation. The managed fault does not simulate a native COM failure.");

            Check("THREAD-DIALOG-PARTIAL-EDIT-RECOVERY-MESSAGE-HAS-RU-EN-PARITY",
                !string.IsNullOrWhiteSpace(editRecoveryMessageRu) &&
                !string.IsNullOrWhiteSpace(editRecoveryMessageEn) &&
                !HelivantaLocalization.ContainsCyrillic(editRecoveryMessageEn) &&
                editRecoveryMessageRu.Contains("Сброс") && editRecoveryMessageEn.Contains("Reset"),
                ref pass, ref fail,
                "The new recovery status is available in both languages and clearly identifies the supported Reset recovery path.");

            string packagingDialogSource205 = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "PackagingThreadDialog.cs"), Encoding.UTF8)
                : string.Empty;
            bool packagingPartialEditFailureFailsClosed =
                packagingDialogSource205.Contains("_editParameterLoadBlocked = true;") &&
                packagingDialogSource205.Contains("PackagingThreadDialog.Edit.ParameterLoadRecoveryBlocked") &&
                packagingDialogSource205.Contains("RecordPackagingBuildAttemptBlocked") &&
                packagingDialogSource205.Contains("RecordPackagingUndoAttemptBlocked") &&
                packagingDialogSource205.Contains("RecordPackagingPreviewAttemptBlocked") &&
                packagingDialogSource205.Contains("ValidateParameters(false);") &&
                packagingDialogSource205.Contains("if (_editParameterLoadBlocked)") &&
                packagingDialogSource205.Contains("_editParameterLoadBlocked = false;");
            Check("CAPNECK-PARTIAL-EDIT-LOAD-FAILURE-FAILS-CLOSED",
                packagingPartialEditFailureFailsClosed, ref pass, ref fail,
                "When a Cap/Neck group partially loads and the previous snapshot is unavailable, Build/Validate/Undo/Preview handlers are blocked until a complete group reload succeeds.");
            bool capNeckEditRecoveryAuditIsLive =
                runtimeAuditAutomationSource.Contains("CAPNECK-PARTIAL-EDIT-LOAD-FAILURE-BLOCKS-CREATE-AND-PREVIEW") &&
                runtimeAuditAutomationSource.Contains("RunCapNeckPartialEditLoadFailureCase(root, runId, result)") &&
                runtimeAuditAutomationSource.Contains("dialog.TryBeginEditByThreadId(firstThreadId") &&
                runtimeAuditAutomationSource.Contains("dialog.TryBeginEditByThreadId(secondThreadId") &&
                runtimeAuditAutomationSource.Contains("PackagingBuildAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("PackagingUndoAttemptBlocked") &&
                runtimeAuditAutomationSource.Contains("PackagingPreviewAttemptBlocked") &&
                packagingDialogSource205.Contains("ValidateParameters(false);") &&
                runtimeAuditAutomationSource.Contains("beforeMetrics.VolumeCm3 - afterMetrics.VolumeCm3");
            Check("RUNTIME-AUDIT-CAPNECK-PARTIAL-EDIT-LOAD-FAILURE-VERIFIES-BLOCK-AND-RELOAD",
                capNeckEditRecoveryAuditIsLive, ref pass, ref fail,
                "The live Inventor test creates two saved Cap/Neck groups, injects a partial UI load failure with the previous snapshot unavailable, invokes actual command handlers and verifies exact saved-group recovery plus unchanged B-rep.");
            string capNeckEditRecoveryRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Edit.ParameterLoadRecoveryBlocked", CultureInfo.InvariantCulture);
            string capNeckEditRecoveryEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Edit.ParameterLoadRecoveryBlocked", CultureInfo.InvariantCulture);
            Check("CAPNECK-PARTIAL-EDIT-RECOVERY-MESSAGE-HAS-RU-EN-PARITY",
                !string.IsNullOrWhiteSpace(capNeckEditRecoveryRu) &&
                !string.IsNullOrWhiteSpace(capNeckEditRecoveryEn) &&
                !HelivantaLocalization.ContainsCyrillic(capNeckEditRecoveryEn) &&
                capNeckEditRecoveryRu.Contains("Повторно") && capNeckEditRecoveryEn.Contains("again"),
                ref pass, ref fail,
                "The Cap/Neck load-failure warning instructs the user in both languages to reload the saved group.");

            string runtimeAuditRunnerPath = !string.IsNullOrWhiteSpace(sourceRoot)
                ? Path.GetFullPath(Path.Combine(sourceRoot, "..", "tools", "run-inventor-runtime-audit.ps1"))
                : string.Empty;
            string runtimeAuditRunnerSource = !string.IsNullOrWhiteSpace(runtimeAuditRunnerPath) &&
                File.Exists(runtimeAuditRunnerPath)
                ? File.ReadAllText(runtimeAuditRunnerPath, Encoding.UTF8)
                : string.Empty;
            bool runtimeAuditUsesTypeLibraryIndependentDispatch =
                runtimeAuditRunnerSource.Contains("[ComImport]") &&
                runtimeAuditRunnerSource.Contains("00020400-0000-0000-C000-000000000046") &&
                runtimeAuditRunnerSource.Contains("GetIDsOfNames(") &&
                runtimeAuditRunnerSource.Contains("dispatch.Invoke(ids[0]") &&
                runtimeAuditRunnerSource.Contains("ComBindingMode: raw IDispatch::GetIDsOfNames/Invoke (no ITypeInfo lookup)");
            Check("RUNTIME-AUDIT-RUNNER-USES-TYPELIB-INDEPENDENT-IDISPATCH",
                runtimeAuditUsesTypeLibraryIndependentDispatch, ref pass, ref fail,
                "The Inventor audit runner must use IDispatch name resolution and invocation directly without requiring ITypeInfo/type-library registration.");
            string runtimeAuditGateSource = !string.IsNullOrWhiteSpace(sourceRoot) &&
                File.Exists(Path.Combine(sourceRoot, "RuntimeAuditAccessGate.cs"))
                ? File.ReadAllText(Path.Combine(sourceRoot, "RuntimeAuditAccessGate.cs"), Encoding.UTF8)
                : string.Empty;
            bool runtimeAuditComEndpointRequiresExplicitOptIn =
                threadAddInDiagnosticSource.Contains("_app == null || !RuntimeAuditAccessGate.IsEnabled()") &&
                runtimeAuditAutomationSource.Contains("if (!RuntimeAuditAccessGate.IsEnabled())") &&
                runtimeAuditGateSource.Contains("EventWaitHandle.OpenExisting(eventName)") &&
                runtimeAuditGateSource.Contains("return gate.WaitOne(0);") &&
                runtimeAuditGateSource.Contains("Local\\Helivanta.RuntimeAudit.Enable") &&
                !runtimeAuditAutomationSource.Contains("[ProgId(");
            Check("RUNTIME-AUDIT-COM-ENDPOINT-IS-OPT-IN-AND-HAS-NO-STANDALONE-PROGID",
                runtimeAuditComEndpointRequiresExplicitOptIn, ref pass, ref fail,
                "Inventor's required Automation property returns null by default; an endpoint is created only while the runner-owned event is set, Run rechecks the event, and the diagnostic class is not separately activatable through a ProgId.");
            Check("RUNTIME-AUDIT-COLLECTS-RIBBON-LIFECYCLE-DIAGNOSTICS",
                runtimeAuditAutomationSource.Contains("\"ribbon-lifecycle.log\"") &&
                runtimeAuditAutomationSource.Contains("CaptureDiagnosticLogOffsets") &&
                ribbonLifecycleDiagnosticsSource.Contains("ribbon-lifecycle.log"),
                ref pass, ref fail,
                "The Runtime Audit export must include the dedicated Ribbon journal so failed language switches can be diagnosed from the collected report.");
            bool runtimeAuditRunnerOwnsShortLivedOptInGate =
                runtimeAuditRunnerSource.Contains("System.Threading.EventWaitHandle]::new(") &&
                runtimeAuditRunnerSource.Contains("Local\\Helivanta.RuntimeAudit.Enable") &&
                runtimeAuditRunnerSource.Contains("[void]$auditGate.Set()") &&
                runtimeAuditRunnerSource.Contains("$auditGate.Reset()") &&
                runtimeAuditRunnerSource.Contains("$auditGate.Dispose()") &&
                runtimeAuditRunnerSource.Contains("RuntimeAuditOptInGate: closed; Automation endpoint unavailable after run") &&
                runtimeAuditRunnerSource.Contains("$automationAfterGateClose = [HelivantaAuditComDispatch]::GetProperty($addIn, 'Automation')");
            Check("RUNTIME-AUDIT-RUNNER-OWNS-AND-CLOSES-OPT-IN-GATE",
                runtimeAuditRunnerOwnsShortLivedOptInGate, ref pass, ref fail,
                "Only the explicit Runtime Audit runner opens the same-session gate, and it resets/disposes the named event in its finally path.");

            bool runtimeAuditAvoidsPowerShellDynamicComBinding =
                runtimeAuditRunnerSource.Contains("[HelivantaAuditComDispatch]::SetProperty($inventor, 'Visible', $true)") &&
                runtimeAuditRunnerSource.Contains("GetPropertyWithArgs($applicationAddIns, 'ItemById'") &&
                runtimeAuditRunnerSource.Contains("GetProperty($addIn, 'Automation')") &&
                runtimeAuditRunnerSource.Contains("InvokeMethod($automation, 'Run'") &&
                !runtimeAuditRunnerSource.Contains("$inventor.") &&
                !runtimeAuditRunnerSource.Contains("$addIn.") &&
                !runtimeAuditRunnerSource.Contains("$automation.");
            Check("RUNTIME-AUDIT-RUNNER-DOES-NOT-USE-POWERSHELL-DYNAMIC-COM-MEMBERS",
                runtimeAuditAvoidsPowerShellDynamicComBinding, ref pass, ref fail,
                "All Inventor Application, ApplicationAddIn and Automation members must go through the raw IDispatch bridge; PowerShell dynamic COM access reintroduces the ITypeInfo failure.");
            bool centralizedSuppressedDiagnosticApi = localizationDiagnosticSource.Contains("LogSuppressedExceptionForDiagnostics(string context, Exception ex)") &&
                localizationDiagnosticSource.Contains("LogExceptionForDiagnostics(\"Suppressed/\"") &&
                localizationDiagnosticSource.Contains("SuppressedExceptionLoggingFailure");
            Check("ITERATION12-FIX7-FIX27-SUPPRESSED-EXCEPTION-DIAGNOSTICS-ARE-CENTRALIZED", centralizedSuppressedDiagnosticApi,
                ref pass, ref fail, "Best-effort catches should route through one guarded diagnostic API rather than silently suppressing exceptions.");
            bool ribbonCommandSinksSurviveLanguageRebuilds =
                threadAddInDiagnosticSource.Contains("_createSinkOwners.TryGetValue(commandName") &&
                threadAddInDiagnosticSource.Contains("_editSinkOwners.TryGetValue(commandName") &&
                threadAddInDiagnosticSource.Contains("button.OnExecute += new ButtonDefinitionSink_OnExecuteEventHandler(OnExecute);") &&
                threadAddInDiagnosticSource.Contains("button.OnExecute += new ButtonDefinitionSink_OnExecuteEventHandler(OnExecuteEdit);") &&
                threadAddInDiagnosticSource.Contains("foreach (KeyValuePair<string, ButtonDefinition> pair in _createSinkOwners)") &&
                threadAddInDiagnosticSource.Contains("foreach (KeyValuePair<string, ButtonDefinition> pair in _editSinkOwners)") &&
                !threadAddInDiagnosticSource.Contains("ThreadProAddIn.BuildRibbonUi.DetachCreateCommand") &&
                !threadAddInDiagnosticSource.Contains("ThreadProAddIn.BuildRibbonUi.DetachEditCommand") &&
                threadAddInDiagnosticSource.Contains("BuildRibbonUi.Panel.delete-failed") &&
                threadAddInDiagnosticSource.Contains("BuildRibbonUi.CreateButton.add-failed") &&
                threadAddInDiagnosticSource.Contains("BuildRibbonUi.EditButton.add-failed");
            Check("RIBBON-LANGUAGE-REBUILDS-KEEP-COMMAND-SINKS-ATTACHED", ribbonCommandSinksSurviveLanguageRebuilds,
                ref pass, ref fail, "A language refresh must reuse stable command COM objects and keep their OnExecute sinks attached; cleanup occurs only during Deactivate.");
            bool ribbonLifecycleJournalIsRealAndBounded =
                ribbonLifecycleDiagnosticsSource.Contains("ribbon-lifecycle.log") &&
                ribbonLifecycleDiagnosticsSource.Contains("DiagnosticLogMaintenance.Append(directory, \"ribbon-lifecycle.log\"") &&
                threadAddInDiagnosticSource.Contains("RibbonLifecycleDiagnostics.WriteException(\"BuildRibbonUi.failed\"") &&
                ribbonLifecycleDiagnosticsSource.Contains("exception.ToString()");
            Check("RIBBON-LIFECYCLE-LOG-RECORDS-REAL-COM-FAILURES", ribbonLifecycleJournalIsRealAndBounded,
                ref pass, ref fail, "The Ribbon lifecycle journal must be emitted by production code and include full COM exception details.");
            bool dialogCriticalSuppressionsAreDiagnosable = threadDialogDiagnosticSource.Contains("ThreadDialog.AttachTransactionEvents.OnUndo") &&
                threadDialogDiagnosticSource.Contains("ThreadDialog.DetachTransactionEvents.OnCommit") &&
                threadDialogDiagnosticSource.Contains("ThreadDialog.QueueModelStateRefresh.UpdateUi") &&
                threadDialogDiagnosticSource.Contains("ThreadDialog.ClearThreadPreview.RefreshSelection") &&
                threadDialogDiagnosticSource.Contains("ThreadDialog.RestoreSelectionAfterPreviewDismissed.RestartInteraction");
            Check("ITERATION12-FIX7-FIX27-DIALOG-UNDO-AND-PREVIEW-FALLBACKS-KEEP-DIAGNOSTIC-CONTEXT", dialogCriticalSuppressionsAreDiagnosable,
                ref pass, ref fail, "Critical event, undo/model refresh and preview/selection recovery catches must retain a stable diagnostic context.");
            bool packagingDialogDisposeCleanupIsIndependent =
                packagingDialogDiagnosticSource.Contains("private void DisposeOwnedResources()") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"PreviewOpacity.Persist\", PersistPackagingPreviewOpacity)") &&
                packagingDialogDiagnosticSource.Contains("private void PersistPackagingPreviewOpacity()") &&
                packagingDialogDiagnosticSource.Contains("if (_disposingOwnedResources || IsDisposed) return;") &&
                packagingDialogDiagnosticSource.Contains("private void TryCleanupOwnedResource(string stage, Action cleanup)") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"UndoStateTimer.Stop\"") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"UndoStateTimer.Dispose\"") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"PreviewRefreshTimer.Stop\"") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"PreviewRefreshTimer.Dispose\"") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"PreviewDialog.Close\"") &&
                packagingDialogDiagnosticSource.Contains("TryCleanupOwnedResource(\"PreviewDialog.Dispose\"") &&
                packagingDialogDiagnosticSource.Contains("_packagingPreview.LastClearSucceeded") &&
                packagingDialogDiagnosticSource.Contains("if (_disposingOwnedResources || IsDisposed) return;") &&
                !packagingDialogDiagnosticSource.Contains("try { _undoStateTimer.Stop(); _undoStateTimer.Dispose(); } catch { }");
            Check("PACKAGING-DIALOG-DISPOSE-CLEANUP-ISOLATES-EACH-OWNED-RESOURCE", packagingDialogDisposeCleanupIsIndependent,
                ref pass, ref fail, "A failure disposing one owned WinForms/Inventor resource must not skip cleanup of the remaining timers, preview dialog, overlay, or tooltip; preview-close callbacks must not touch an already disposing owner.");

            string packagingSingleEdgeRequiredRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.SingleEdgeRequired", CultureInfo.InvariantCulture);
            string packagingSingleEdgeRequiredEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.SingleEdgeRequired", CultureInfo.InvariantCulture);
            string packagingSelectionTransferRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.SelectionTransferFailed", CultureInfo.InvariantCulture);
            string packagingSelectionTransferEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.SelectionTransferFailed", CultureInfo.InvariantCulture);
            bool capNeckModePreservesSingleSelectedEdge = threadDialogDiagnosticSource.Contains("TryPrepareNativeSelectionForPackagingMode") &&
                threadDialogDiagnosticSource.Contains("_sel.Targets[0].SelectedEdge") &&
                threadDialogDiagnosticSource.Contains("nativeSelection.Select(edgeFromThreadPicker)") &&
                threadDialogDiagnosticSource.Contains("ThreadDialog.ModeSwitch.SingleEdgeRequired") &&
                !string.IsNullOrWhiteSpace(packagingSingleEdgeRequiredRu) && !string.IsNullOrWhiteSpace(packagingSingleEdgeRequiredEn) &&
                !string.IsNullOrWhiteSpace(packagingSelectionTransferRu) && !string.IsNullOrWhiteSpace(packagingSelectionTransferEn) &&
                !packagingSingleEdgeRequiredEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !packagingSelectionTransferEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("ITERATION12-FIX7-FIX28-CAP-NECK-MODE-PRESERVES-ONE-SELECTED-EDGE", capNeckModePreservesSingleSelectedEdge,
                ref pass, ref fail, "Switching from the normal picker must carry one selected Edge into Inventor SelectSet, reject ambiguous selections, retain RU/EN resources, and preserve the selection on failure.");
            int packagingEditEntryStart = threadDialogDiagnosticSource.IndexOf("private bool TryBeginEditFromLoadedGroup", StringComparison.Ordinal);
            int packagingEditEntryEnd = packagingEditEntryStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("string buildZonesError;", packagingEditEntryStart, StringComparison.Ordinal);
            string packagingEditEntrySource = packagingEditEntryStart >= 0 && packagingEditEntryEnd > packagingEditEntryStart
                ? threadDialogDiagnosticSource.Substring(packagingEditEntryStart, packagingEditEntryEnd - packagingEditEntryStart)
                : string.Empty;
            int packagingModeEntryStart = threadDialogDiagnosticSource.IndexOf("private void SetPackagingMode(bool enabled)", StringComparison.Ordinal);
            int packagingModeEntryEnd = packagingModeEntryStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("private void ShowThemedModeSwitchWarning", packagingModeEntryStart, StringComparison.Ordinal);
            string packagingModeEntrySource = packagingModeEntryStart >= 0 && packagingModeEntryEnd > packagingModeEntryStart
                ? threadDialogDiagnosticSource.Substring(packagingModeEntryStart, packagingModeEntryEnd - packagingModeEntryStart)
                : string.Empty;
            bool capNeckMetadataEditSkipsNewEdgePickerRequirement =
                packagingEditEntrySource.Contains("_packagingModeEntryForMetadataEdit = true;") &&
                packagingEditEntrySource.Contains("SetPackagingMode(true);") &&
                packagingModeEntrySource.Contains("bool enteringForMetadataEdit = _packagingModeEntryForMetadataEdit;") &&
                packagingModeEntrySource.Contains("if (!enteringForMetadataEdit && logicalTargetCount > 1)") &&
                packagingModeEntrySource.Contains("if (!enteringForMetadataEdit)") &&
                packagingModeEntrySource.Contains("TryPrepareNativeSelectionForPackagingMode(edgeToPreserve, hasLogicalSelection, out selectionTransitionError)") &&
                packagingModeEntrySource.Contains("packaging-mode-entered-for-metadata-edit");
            Check("CAP-NECK-METADATA-EDIT-DOES-NOT-REQUIRE-CIRCULAR-EDGE-SELECTION",
                capNeckMetadataEditSkipsNewEdgePickerRequirement, ref pass, ref fail,
                "Editing an existing metadata-backed cap/neck feature enters by its selected Browser/feature object; the one-edge SelectSet requirement remains only for new builds.");
            string leaveCurrentEditTitleRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditTitle", CultureInfo.InvariantCulture);
            string leaveCurrentEditMessageRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditMessage", CultureInfo.InvariantCulture);
            string leaveCurrentEditTitleEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditTitle", CultureInfo.InvariantCulture);
            string leaveCurrentEditMessageEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditMessage", CultureInfo.InvariantCulture);
            string leaveStandardEditForCapNeckRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveStandardEditForCapNeckMessage", CultureInfo.InvariantCulture);
            string leaveStandardEditForCapNeckEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveStandardEditForCapNeckMessage", CultureInfo.InvariantCulture);
            string leaveTaperedEditForCapNeckRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveTaperedEditForCapNeckMessage", CultureInfo.InvariantCulture);
            string leaveTaperedEditForCapNeckEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveTaperedEditForCapNeckMessage", CultureInfo.InvariantCulture);
            string leaveCurrentEditCancelledRu = russianCatalog == null ? null : russianCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditCancelled", CultureInfo.InvariantCulture);
            string leaveCurrentEditCancelledEn = englishCatalog == null ? null : englishCatalog.GetString(
                "ThreadDialog.ModeSwitch.LeaveCurrentEditCancelled", CultureInfo.InvariantCulture);
            int leaveStandardEditStart = threadDialogDiagnosticSource.IndexOf("private bool TryLeaveStandardEditForCapNeckMode()", StringComparison.Ordinal);
            int leaveStandardEditEnd = leaveStandardEditStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("private void SetPackagingMode(bool enabled)", leaveStandardEditStart, StringComparison.Ordinal);
            string leaveStandardEditSource = leaveStandardEditStart >= 0 && leaveStandardEditEnd > leaveStandardEditStart
                ? threadDialogDiagnosticSource.Substring(leaveStandardEditStart, leaveStandardEditEnd - leaveStandardEditStart)
                : string.Empty;
            int switchToCreateStart = threadDialogDiagnosticSource.IndexOf("private void SwitchToCreateMode(", StringComparison.Ordinal);
            int switchToCreateEnd = switchToCreateStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("private void LoadEditParameters(", switchToCreateStart, StringComparison.Ordinal);
            string switchToCreateSource = switchToCreateStart >= 0 && switchToCreateEnd > switchToCreateStart
                ? threadDialogDiagnosticSource.Substring(switchToCreateStart, switchToCreateEnd - switchToCreateStart)
                : string.Empty;
            bool taperedModeIsClearedOnlyAfterConfirmation =
                switchToCreateSource.IndexOf("prompt.ShowDialog(this)", StringComparison.Ordinal) >= 0 &&
                switchToCreateSource.IndexOf("prompt.ShowDialog(this)", StringComparison.Ordinal) <
                    switchToCreateSource.IndexOf("if (clearTaperedModeAfterConfirmation && chTapered != null && chTapered.Checked)", StringComparison.Ordinal) &&
                switchToCreateSource.IndexOf("ApplyTaperModeUI(false);", StringComparison.Ordinal) <
                    switchToCreateSource.IndexOf("ResetDialogDefaults();", StringComparison.Ordinal) &&
                switchToCreateSource.Contains("if (prompt.ShowDialog(this) != DialogResult.Yes) return;") &&
                switchToCreateSource.Contains("try { chTapered.Checked = false; }") &&
                switchToCreateSource.Contains("_editMode = false;") &&
                !switchToCreateSource.Contains("ThreadEditService.") &&
                !switchToCreateSource.Contains("ThreadBuilder.Build(") &&
                !switchToCreateSource.Contains("Close();") &&
                !switchToCreateSource.Contains("this.Dispose(");
            bool capNeckModeCanReplaceActiveStandardEdit =
                packagingModeEntrySource.Contains("if (enabled && _editMode && !_packagingModeEntryForMetadataEdit") &&
                packagingModeEntrySource.Contains("TryLeaveStandardEditForCapNeckMode()") &&
                packagingModeEntrySource.IndexOf("TryLeaveStandardEditForCapNeckMode()", StringComparison.Ordinal) <
                    packagingModeEntrySource.IndexOf("else if (enabled && chTapered != null && chTapered.Checked)", StringComparison.Ordinal) &&
                leaveStandardEditSource.Contains("string messageResourceKey = wasTapered") &&
                leaveStandardEditSource.Contains("ThreadDialog.ModeSwitch.LeaveTaperedEditForCapNeckMessage") &&
                leaveStandardEditSource.Contains("ThreadDialog.ModeSwitch.LeaveStandardEditForCapNeckMessage") &&
                leaveStandardEditSource.Contains("SwitchToCreateMode(\"ThreadDialog.ModeSwitch.LeaveCurrentEditTitle\", messageResourceKey,") &&
                leaveStandardEditSource.Contains("clearTaperedModeAfterConfirmation: wasTapered") &&
                leaveStandardEditSource.Contains("if (_editMode)") &&
                leaveStandardEditSource.Contains("capneck-mode-switch-edit-discard-declined") &&
                packagingEditEntrySource.Contains("if (_editMode && !TryLeaveStandardEditForCapNeckMode())") &&
                packagingEditEntrySource.IndexOf("TryLeaveStandardEditForCapNeckMode()", StringComparison.Ordinal) <
                    packagingEditEntrySource.IndexOf("_packagingModeEntryForMetadataEdit = true;", StringComparison.Ordinal) &&
                leaveStandardEditSource.Contains("committedGeometryPreserved=true; sameDialog=true") &&
                taperedModeIsClearedOnlyAfterConfirmation &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditTitleRu) &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditMessageRu) &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditTitleEn) &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditMessageEn) &&
                !string.IsNullOrWhiteSpace(leaveStandardEditForCapNeckRu) &&
                !string.IsNullOrWhiteSpace(leaveStandardEditForCapNeckEn) &&
                !string.IsNullOrWhiteSpace(leaveTaperedEditForCapNeckRu) &&
                !string.IsNullOrWhiteSpace(leaveTaperedEditForCapNeckEn) &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditCancelledRu) &&
                !string.IsNullOrWhiteSpace(leaveCurrentEditCancelledEn) &&
                !leaveCurrentEditTitleEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !leaveCurrentEditMessageEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !leaveStandardEditForCapNeckEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !leaveTaperedEditForCapNeckEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F')) &&
                !leaveCurrentEditCancelledEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("CAP-NECK-MODE-SWITCH-LEAVES-STANDARD-EDIT-IN-PLACE-WITH-CONFIRMATION",
                capNeckModeCanReplaceActiveStandardEdit, ref pass, ref fail,
                "Both Cap/Neck entry paths confirm before clearing unapplied values; tapered mode clears only after Yes, persisted geometry is untouched, and the same form continues with RU/EN resources.");
            int packagingToggleHandlerStart = threadDialogDiagnosticSource.IndexOf("chPackagingMode.CheckedChanged +=", StringComparison.Ordinal);
            int packagingToggleHandlerEnd = packagingToggleHandlerStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("var modeSwitchPanel", packagingToggleHandlerStart, StringComparison.Ordinal);
            string packagingToggleHandlerSource = packagingToggleHandlerStart >= 0 && packagingToggleHandlerEnd > packagingToggleHandlerStart
                ? threadDialogDiagnosticSource.Substring(packagingToggleHandlerStart, packagingToggleHandlerEnd - packagingToggleHandlerStart)
                : string.Empty;
            int selectedPackagingEditStart = threadDialogDiagnosticSource.IndexOf("private bool TryBeginSelectedPackagingMetadataEdit(out bool handled, out string error)", StringComparison.Ordinal);
            int selectedPackagingEditEnd = selectedPackagingEditStart < 0 ? -1 : threadDialogDiagnosticSource.IndexOf("private static int SafeCurrentSelectSetCount", selectedPackagingEditStart, StringComparison.Ordinal);
            string selectedPackagingEditSource = selectedPackagingEditStart >= 0 && selectedPackagingEditEnd > selectedPackagingEditStart
                ? threadDialogDiagnosticSource.Substring(selectedPackagingEditStart, selectedPackagingEditEnd - selectedPackagingEditStart)
                : string.Empty;
            bool taperedEditCanReachCapNeckModeSwitch =
                packagingToggleHandlerSource.Contains("TryBeginSelectedPackagingMetadataEdit(") &&
                packagingToggleHandlerSource.Contains("SetPackagingMode(chPackagingMode.Checked);") &&
                !packagingToggleHandlerSource.Contains("CapNeckBlockedByTapered") &&
                selectedPackagingEditSource.Contains("if (_editMode)") &&
                selectedPackagingEditSource.Contains("continueToSetPackagingMode=true") &&
                packagingModeEntrySource.Contains("if (enabled && _editMode && !_packagingModeEntryForMetadataEdit") &&
                packagingModeEntrySource.Contains("TryLeaveStandardEditForCapNeckMode()") &&
                packagingModeEntrySource.IndexOf("TryLeaveStandardEditForCapNeckMode()", StringComparison.Ordinal) <
                    packagingModeEntrySource.IndexOf("else if (enabled && chTapered != null && chTapered.Checked)", StringComparison.Ordinal) &&
                leaveStandardEditSource.Contains("SwitchToCreateMode(\"ThreadDialog.ModeSwitch.LeaveCurrentEditTitle\", messageResourceKey,") &&
                leaveStandardEditSource.Contains("clearTaperedModeAfterConfirmation: wasTapered");
            Check("CAP-NECK-MODE-SWITCH-IS-REACHABLE-WHILE-TAPERED-THREAD-IS-BEING-EDITED",
                taperedEditCanReachCapNeckModeSwitch, ref pass, ref fail,
                "The visible toggle and selected Cap/Neck metadata route both reach the shared confirmation before tapered mutual-exclusion handling; ordinary active-edit selection falls through to SetPackagingMode.");
            bool capNeckEditCannotBeDetachedByModeToggle =
                packagingToggleHandlerSource.Contains("!chPackagingMode.Checked && _packagingMode") &&
                packagingToggleHandlerSource.Contains("_packagingEmbeddedDialog.IsEditingExistingThread") &&
                packagingToggleHandlerSource.Contains("SetPackagingModeToggle(true);") &&
                packagingToggleHandlerSource.Contains("ThreadDialog.ModeSwitch.CapNeckEditMustBeEnded") &&
                packagingDialogDiagnosticSource.Contains("internal bool IsEditingExistingThread =>") &&
                packagingDialogDiagnosticSource.Contains("!string.IsNullOrWhiteSpace(_editThreadId)") &&
                packagingDialogDiagnosticSource.Contains("_editSavedSnapshot != null");
            Check("CAP-NECK-EDIT-MODE-TOGGLE-CANNOT-DETACH-AND-EMPTY-MAIN-DIALOG",
                capNeckEditCannotBeDetachedByModeToggle, ref pass, ref fail,
                "Turning Cap/Neck mode off during a metadata edit restores the toggle and explains that New Thread must explicitly end edit mode before detaching the embedded controls.");
            bool autoPitchConfirmationUsesCenteredTextButtons =
                packagingDialogDiagnosticSource.Contains("CreateEmbeddedTextActionButton(HelivantaLocalization.Get(\"PackagingThreadDialog.AutoPitch.EnableButton\"), true, 190)") &&
                packagingDialogDiagnosticSource.Contains("CreateEmbeddedTextActionButton(HelivantaLocalization.Get(\"PackagingThreadDialog.AutoPitch.DoNotEnableButton\"), false, 150)") &&
                packagingDialogDiagnosticSource.Contains("dialog.ClientSize = new Size(500, 230)") &&
                threadDialogDiagnosticSource.Contains("TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter") &&
                threadDialogDiagnosticSource.Contains("CreateEmbeddedTextActionButton(string text, bool primary, int width)");
            Check("AUTO-PITCH-CONFIRMATION-BUTTON-CAPTIONS-ARE-CENTERED-AND-FIT-RU-EN",
                autoPitchConfirmationUsesCenteredTextButtons, ref pass, ref fail,
                "The auto-pitch confirmation uses wider text-only buttons with true horizontal/vertical centering and ellipsis only as a final safety fallback.");
            bool selectedMetadataResolutionAllowsManyUiObjectsButOneLogicalThread =
                threadMetadataStoreSource.Contains("TryResolveSelectedThreadMetadata(PartDocument doc") &&
                threadMetadataStoreSource.Contains("nativeSelection.Count") &&
                threadMetadataStoreSource.Contains("TryGetSelectedBrowserNodes(doc, out browserNodes, out browserError)") &&
                threadMetadataStoreSource.Contains("var ids = new HashSet<string>(StringComparer.Ordinal)") &&
                threadMetadataStoreSource.Contains("if (ids.Count > 1)") &&
                threadMetadataStoreSource.Contains("distinctThreadIds=1");
            Check("THREAD-SELECTION-RESOLVES-ONE-UNIQUE-METADATA-GROUP-ACROSS-MULTIPLE-SELECTSET-AND-BROWSER-NODES",
                selectedMetadataResolutionAllowsManyUiObjectsButOneLogicalThread, ref pass, ref fail,
                "Several selected browser/SelectSet objects are accepted only when complete metadata traversal identifies one unique ThreadId; multiple groups and incomplete traversal fail closed.");
            bool nativeThreadSelectionRequiresOneUniqueComFeature =
                nativeThreadConversionDiagnosticSource.Contains("TryResolveSelectedFeature(PartDocument doc") &&
                nativeThreadConversionDiagnosticSource.Contains("SameComObject(existing, resolved)") &&
                nativeThreadConversionDiagnosticSource.Contains("if (matches.Count > 1)") &&
                nativeThreadConversionDiagnosticSource.Contains("distinctNativeThreadFeatures=1") &&
                nativeThreadConversionDiagnosticSource.Contains("foreach (BrowserNode child in node.BrowserNodes)");
            Check("NATIVE-THREAD-SELECTION-RESOLVES-ONE-UNIQUE-FEATURE-COM-IDENTITY",
                nativeThreadSelectionRequiresOneUniqueComFeature, ref pass, ref fail,
                "A native cosmetic ThreadFeature may be selected through multiple SelectSet objects or a parent BrowserNode, but conversion requires one unique COM feature identity.");
            bool capNeckModeSwitchRoutesSelectedMetadataGroupToEdit =
                threadDialogDiagnosticSource.Contains("TryBeginSelectedPackagingMetadataEdit(out bool handled, out string error)") &&
                threadDialogDiagnosticSource.Contains("TryResolveSelectedThreadMetadata(doc, out threadId") &&
                threadDialogDiagnosticSource.Contains("selectedGroup.Record.IsPackagingThread") &&
                threadDialogDiagnosticSource.Contains("TryBeginEditByThreadId(threadId, out error)") &&
                threadDialogDiagnosticSource.Contains("TryBeginSelectedPackagingMetadataEdit(") &&
                threadDialogDiagnosticSource.Contains("if (metadataEditHandled)");
            Check("CAP-NECK-SELECTED-BROWSER-GROUP-ENTERS-METADATA-EDIT-BEFORE-NEW-EDGE-PICKER",
                capNeckModeSwitchRoutesSelectedMetadataGroupToEdit, ref pass, ref fail,
                "When the user switches the Cap/Neck mode with an existing CapNeck feature selected, the existing metadata group opens for editing before the one-circular-edge validator for a new build.");
            bool editCommandPrioritizesHelivantaMetadataOverNativeThreadFeature =
                threadAddInDiagnosticSource.Contains("TryResolveSelectedThreadMetadata(doc, out threadId") &&
                threadAddInDiagnosticSource.Contains("TryResolveSelectedFeature(doc, out nativeFeature") &&
                threadAddInDiagnosticSource.Contains("selectedObjectIsHelivantaMetadata") &&
                threadAddInDiagnosticSource.Contains("if (!selectedObjectIsHelivantaMetadata &&");
            Check("EDIT-SELECTION-PREFERS-HELIVANTA-METADATA-AND-RESOLVES-NATIVE-THREAD-INDEPENDENTLY",
                editCommandPrioritizesHelivantaMetadataOverNativeThreadFeature, ref pass, ref fail,
                "The edit command does not misroute metadata-backed Helivanta operations into native conversion and resolves cosmetic ThreadFeature through an independent unique-selection path.");
            bool editCommandCarriesResolvedMetadataIdWithoutSecondComRead =
                threadAddInDiagnosticSource.Contains("private sealed class SelectedMetadataEditTarget") &&
                threadAddInDiagnosticSource.Contains("handoffUsesValidatedThreadId=true") &&
                threadAddInDiagnosticSource.Contains("ThreadProAddIn.OpenEditForSelection.MetadataIdHandoff") &&
                threadAddInDiagnosticSource.Contains("_dlg.TryBeginEditByThreadId(selectedHelivantaThreadId, out error)") &&
                threadAddInDiagnosticSource.Contains("dialog.TryBeginEditByThreadId(selectedHelivantaThreadId, out beginError)");
            Check("EDIT-SELECTION-HANDOFF-USES-ALREADY-RESOLVED-THREADID-WITHOUT-REPEATING-COM-SELECTION-LOOKUP",
                editCommandCarriesResolvedMetadataIdWithoutSecondComRead, ref pass, ref fail,
                "The selected logical ThreadId survives the modeless prompt and enters edit by id; transient COM BrowserNode/SelectSet proxies are not re-resolved during launch.");
            bool capNeckUnresolvedBrowserFeatureDoesNotFallIntoNewEdgePicker =
                threadDialogDiagnosticSource.Contains("TryGetSelectedBrowserNodes(") &&
                threadDialogDiagnosticSource.Contains("bool hasTreeSelection = browserSelectionReadable && selectedBrowserNodes.Count > 0") &&
                threadDialogDiagnosticSource.Contains("bool hasNonEdgeSelectSetObject = false") &&
                threadDialogDiagnosticSource.Contains("if (ambiguous || hasTreeSelection || hasNonEdgeSelectSetObject ||") &&
                threadDialogDiagnosticSource.Contains("packaging-metadata-edit-selection-not-resolved") &&
                threadDialogDiagnosticSource.Contains("edgeOnlySelection=true");
            Check("CAP-NECK-UNRESOLVED-BROWSER-FEATURE-FAILS-CLEARLY-INSTEAD-OF-REQUESTING-A-NEW-CIRCULAR-EDGE",
                capNeckUnresolvedBrowserFeatureDoesNotFallIntoNewEdgePicker, ref pass, ref fail,
                "A selected BrowserNode or non-edge feature that cannot be mapped to metadata is not silently routed to new-build edge picking; only empty or edge-only selection enters creation.");
            bool nativeTaperedPrepareLogsEveryRejectedStage =
                nativeThreadConversionDiagnosticSource.Contains("bool preparationSucceeded = false") &&
                nativeThreadConversionDiagnosticSource.Contains("preparationSucceeded = true;") &&
                nativeThreadConversionDiagnosticSource.Contains("finally") &&
                nativeThreadConversionDiagnosticSource.Contains("NativeThreadConversionService.TryPrepare.Rejected") &&
                nativeThreadConversionDiagnosticSource.Contains("threadInfoClrType=");
            Check("NATIVE-THREAD-CONVERSION-RECORDS-EXACT-PREFLIGHT-STAGE-AND-THREADINFO-TYPE-ON-EVERY-REJECTION",
                nativeTaperedPrepareLogsEveryRejectedStage, ref pass, ref fail,
                "Every TryPrepare early return is logged with feature name, raw ThreadInfo CLR type, stage, and user-visible rejection; runtime failures can be distinguished from selection failures.");
            bool taperedDirectionUsesDedicatedConicalResolution =
                nativeThreadConversionDiagnosticSource.Contains("TryResolveStartEdge(app, feature, !nativeInternal") &&
                nativeThreadConversionDiagnosticSource.Contains("if (feature.DirectionReversed && !isTapered)") &&
                nativeThreadConversionDiagnosticSource.Contains("NativeThreadConversionService.TryPrepare.TaperedDirectionReversed") &&
                nativeTaperedThreadBuilderDiagnosticSource.Contains("GetDirectionReversedForFace(host.ConeFace, source.U)") &&
                nativeThreadConversionDiagnosticSource.Contains("DirectionReversed = feature.DirectionReversed") &&
                nativeThreadConversionDiagnosticSource.Contains("stage = isTapered") &&
                nativeThreadConversionDiagnosticSource.Contains("threadInfoClrType=");
            Check("NATIVE-TAPERED-THREAD-PREFLIGHT-RETAINS-DIRECTION-STATE-FOR-DIAGNOSTICS",
                taperedDirectionUsesDedicatedConicalResolution, ref pass, ref fail,
                "The tapered path resolves native reversal through its own start-edge and generated-cone axes; the cylinder-only path remains separately guarded.");
            string packagingInlineFailureRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture);
            string packagingInlineFailureEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Error.InlineFailure", CultureInfo.InvariantCulture);
            bool packagingOverlapReasonUsesExistingInlineTemplate =
                !packagingDialogDiagnosticSource.Contains("PackagingThreadDialog.InlineFailure") &&
                packagingDialogDiagnosticSource.Contains("PackagingThreadDialog.Error.InlineFailure") &&
                !string.IsNullOrWhiteSpace(packagingInlineFailureRu) && packagingInlineFailureRu.Contains("{0}") && packagingInlineFailureRu.Contains("{1}") &&
                !string.IsNullOrWhiteSpace(packagingInlineFailureEn) && packagingInlineFailureEn.Contains("{0}") && packagingInlineFailureEn.Contains("{1}") &&
                !packagingInlineFailureEn.Any(ch => (ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'));
            Check("ITERATION12-FIX7-FIX30-CAP-NECK-OVERLAP-REASON-REACHES-UI", packagingOverlapReasonUsesExistingInlineTemplate,
                ref pass, ref fail, "The outer inline failure template must exist in both .resx catalogs so a typed overlap error keeps its detailed bilingual reason and diagnostic-log path.");
            string packagingEditFailureSource = packagingDialogDiagnosticSource;
            string threadBuilderVentFailureSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8)
                : string.Empty;
            bool unsafeVentPlacementCannotFailSilentlyDuringEdit =
                packagingEditFailureSource.Contains("if (!ThreadEditService.TryReplacePackaging(") &&
                packagingEditFailureSource.Contains("PackagingThreadDialog.Edit.ReplacingGroup") &&
                packagingEditFailureSource.Contains("PackagingThreadDialog.Error.InlineFailure") &&
                packagingEditFailureSource.Contains("RevealSummaryAfterFailure();") &&
                threadBuilderVentFailureSource.Contains("ventSlotError.Code == HelivantaErrorCode.VentSlotPlacementUnsafe");
            Check("CAP-NECK-EDIT-UNSAFE-VENT-PLACEMENT-IS-SHOWN-INSTEAD-OF-SILENTLY-ACCEPTED",
                unsafeVentPlacementCannotFailSilentlyDuringEdit, ref pass, ref fail,
                "An endpoint-unsafe vent-slot layout must remain a failed edit, be localized in the embedded summary and never be reported as a successful replacement.");
            bool capNeckEditEnablingVentSlotsAppliesDefaults =
                packagingEditFailureSource.Contains("_editSavedSnapshot != null") &&
                packagingEditFailureSource.Contains("!_editSavedSnapshot.VentSlotsEnabled") &&
                packagingEditFailureSource.Contains("GetDefaultCount(false)") &&
                packagingEditFailureSource.Contains("GetDefaultWidthDegrees(false)") &&
                packagingEditFailureSource.Contains("_updatingVentSlotDefaults = true") &&
                packagingEditFailureSource.Contains("_ventSlotCountEditedByUser = false") &&
                packagingEditFailureSource.Contains("_ventSlotWidthEditedByUser = false");
            Check("CAP-NECK-EDIT-ENABLING-VENT-SLOTS-RESTORES-DEFAULT-2-CHANNELS-14-DEGREES",
                capNeckEditEnablingVentSlotsAppliesDefaults, ref pass, ref fail,
                "When an existing Cap/Neck edit had no vent channels, activating the feature must initialize 2 channels and 14 degrees without marking programmatic defaults as user edits; saved active channels remain unchanged.");
            int undoCallbackStart = threadDialogDiagnosticSource.IndexOf("private void TransactionEvents_OnUndo(", StringComparison.Ordinal);
            int redoCallbackStart = threadDialogDiagnosticSource.IndexOf("private void TransactionEvents_OnRedo(", undoCallbackStart, StringComparison.Ordinal);
            int commitCallbackStart = threadDialogDiagnosticSource.IndexOf("private void TransactionEvents_OnCommit(", redoCallbackStart, StringComparison.Ordinal);
            int moveBuildToRedoStart = threadDialogDiagnosticSource.IndexOf("private void MoveLastActiveBuildToRedo()", commitCallbackStart, StringComparison.Ordinal);
            string undoCallbackSource = undoCallbackStart >= 0 && redoCallbackStart > undoCallbackStart
                ? threadDialogDiagnosticSource.Substring(undoCallbackStart, redoCallbackStart - undoCallbackStart) : string.Empty;
            string redoCallbackSource = redoCallbackStart >= 0 && commitCallbackStart > redoCallbackStart
                ? threadDialogDiagnosticSource.Substring(redoCallbackStart, commitCallbackStart - redoCallbackStart) : string.Empty;
            string commitCallbackSource = commitCallbackStart >= 0 && moveBuildToRedoStart > commitCallbackStart
                ? threadDialogDiagnosticSource.Substring(commitCallbackStart, moveBuildToRedoStart - commitCallbackStart) : string.Empty;
            bool transactionComCallbacksAreExceptionContained =
                undoCallbackSource.Contains("try\n") && undoCallbackSource.Contains("catch (Exception ex)") &&
                undoCallbackSource.Contains("ThreadDialog.TransactionEvents_OnUndo.") &&
                undoCallbackSource.Contains("CallbackFailed.") &&
                redoCallbackSource.Contains("try\n") && redoCallbackSource.Contains("catch (Exception ex)") &&
                redoCallbackSource.Contains("ThreadDialog.TransactionEvents_OnRedo.") &&
                redoCallbackSource.Contains("CallbackFailed.") &&
                commitCallbackSource.Contains("try\n") && commitCallbackSource.Contains("catch (Exception ex)") &&
                commitCallbackSource.Contains("ThreadDialog.TransactionEvents_OnCommit.") &&
                commitCallbackSource.Contains("CallbackFailed.");
            Check("INVENTOR-TRANSACTION-COM-CALLBACKS-CONTAIN-EXCEPTIONS-AND-LOG-FAILURES",
                transactionComCallbacksAreExceptionContained, ref pass, ref fail,
                "OnUndo, OnRedo and OnCommit must not allow UI/metadata/runtime exceptions to escape through Inventor's COM event sink; pending classification is reset and each failure is diagnosed.");
            int packagingUndoStart = packagingDialogDiagnosticSource.IndexOf("private void UndoLastPackagingBuild()", StringComparison.Ordinal);
            int packagingUndoEnd = packagingUndoStart < 0 ? -1 : packagingDialogDiagnosticSource.IndexOf("private void CloseDialog()", packagingUndoStart, StringComparison.Ordinal);
            string packagingUndoSource = packagingUndoStart >= 0 && packagingUndoEnd > packagingUndoStart
                ? packagingDialogDiagnosticSource.Substring(packagingUndoStart, packagingUndoEnd - packagingUndoStart) : string.Empty;
            bool packagingUndoDoesNotClaimUnverifiedSelection =
                packagingUndoSource.Contains("bool selectionRestored = undoingEdit ||") &&
                packagingUndoSource.Contains("RestoreSelectedEdgeAfterAbort(edgeDocument, edgeToRestore, null, 0)") &&
                packagingUndoSource.Contains("if (!undoingEdit && !selectionRestored)") &&
                packagingUndoSource.Contains("PackagingThreadDialog.Undo.SelectionRestoreWarning") &&
                packagingUndoSource.Contains("selectionRestored\n                        ? ");
            Check("CAP-NECK-UNDO-REPORTS-SELECTION-RESTORE-FAILURE-AFTER-GEOMETRY-UNDO",
                packagingUndoDoesNotClaimUnverifiedSelection, ref pass, ref fail,
                "A successful geometry Undo with an unverified prior-edge selection must report that selection was not restored instead of using the success text that promises it was reselected.");
            string packagingUndoSelectionWarningRu = russianCatalog == null ? null : russianCatalog.GetString(
                "PackagingThreadDialog.Undo.SelectionRestoreWarning", CultureInfo.InvariantCulture);
            string packagingUndoSelectionWarningEn = englishCatalog == null ? null : englishCatalog.GetString(
                "PackagingThreadDialog.Undo.SelectionRestoreWarning", CultureInfo.InvariantCulture);
            bool packagingUndoSelectionWarningIsLocalized =
                !string.IsNullOrWhiteSpace(packagingUndoSelectionWarningRu) &&
                !string.IsNullOrWhiteSpace(packagingUndoSelectionWarningEn) &&
                !System.Text.RegularExpressions.Regex.IsMatch(packagingUndoSelectionWarningEn, "[\u0400-\u052F]") &&
                packagingUndoSelectionWarningEn.Contains("undone") && packagingUndoSelectionWarningEn.Contains("manually");
            Check("CAP-NECK-UNDO-SELECTION-RESTORE-WARNING-HAS-COMPLETE-RU-EN-RESOURCES",
                packagingUndoSelectionWarningIsLocalized, ref pass, ref fail,
                "When geometry undo succeeds but selection restoration cannot be verified, the dialog must show complete Russian and English recovery guidance.");
            bool packagingTimerCallbacksContainAndStopAfterFailures =
                packagingDialogDiagnosticSource.Contains("packaging-preview-refresh-timer") &&
                packagingDialogDiagnosticSource.Contains("packaging-undo-state-timer") &&
                packagingDialogDiagnosticSource.Contains("_previewRefreshTimer.Stop();") &&
                packagingDialogDiagnosticSource.Contains("_undoStateTimer.Stop();") &&
                packagingDialogDiagnosticSource.Contains("WriteException(") &&
                packagingDialogDiagnosticSource.Contains("packaging-undo-state-timer-stopped");
            Check("CAP-NECK-MODELESS-TIMER-CALLBACKS-LOG-AND-STOP-ON-UNEXPECTED-FAILURE",
                packagingTimerCallbacksContainAndStopAfterFailures, ref pass, ref fail,
                "Preview and Undo-state timer callbacks must contain unexpected exceptions, record diagnostics, and stop the failing timer rather than silently swallow or retry the same failure every interval.");
            bool packagingModeLayoutIsBatched = threadDialogDiagnosticSource.Contains("CompletePackagingModeLayoutTransition();") &&
                threadDialogDiagnosticSource.Contains("private void CompletePackagingModeLayoutTransition()") &&
                threadDialogDiagnosticSource.Contains("ResizeSectionWidths(deferLayout: true)") &&
                !threadDialogDiagnosticSource.Substring(threadDialogDiagnosticSource.IndexOf("private void SetPackagingMode(bool enabled)", StringComparison.Ordinal),
                    threadDialogDiagnosticSource.IndexOf("private void ShowThemedModeSwitchWarning", StringComparison.Ordinal) -
                    threadDialogDiagnosticSource.IndexOf("private void SetPackagingMode(bool enabled)", StringComparison.Ordinal)).Contains("RefreshVerticalLayout();");
            Check("ITERATION12-FIX7-FIX30-CAP-NECK-MODE-LAYOUT-IS-BATCHED", packagingModeLayoutIsBatched,
                ref pass, ref fail, "Nested visibility changes should be laid out once after reparenting without recomputing the main form ClientSize on each switch.");

            // 2.6.160: use the proven JIS axis-profile + Coil.TaperAngle construction path;
            // retain the normative GOST circular rounds as tightly bounded line chords.
            double gostFilletChordErrorCm = TaperedThreadGeometryPolicy.GostFilletMaxChordErrorCm;
            List<double[]> quarterArc = TaperedThreadGeometryPolicy.ApproximateCircularArc(
                new[] { 1.0, 0.0 }, new[] { Math.Sqrt(0.5), Math.Sqrt(0.5) }, new[] { 0.0, 1.0 }, gostFilletChordErrorCm);
            List<double[]> mirroredQuarterArc = TaperedThreadGeometryPolicy.ApproximateCircularArc(
                new[] { -1.0, 0.0 }, new[] { -Math.Sqrt(0.5), Math.Sqrt(0.5) }, new[] { 0.0, 1.0 }, gostFilletChordErrorCm);
            double maximumSagittaCm = 0.0;
            for (int arcIndex = 0; arcIndex + 1 < quarterArc.Count; arcIndex++)
            {
                double chord = Math.Sqrt(Math.Pow(quarterArc[arcIndex + 1][0] - quarterArc[arcIndex][0], 2.0) +
                    Math.Pow(quarterArc[arcIndex + 1][1] - quarterArc[arcIndex][1], 2.0));
                maximumSagittaCm = Math.Max(maximumSagittaCm, 1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - chord * chord / 4.0)));
            }
            bool mirroredSymmetrically = quarterArc.Count == mirroredQuarterArc.Count &&
                quarterArc.Select((point, index) => Math.Abs(point[0] + mirroredQuarterArc[index][0]) < 1e-10 &&
                    Math.Abs(point[1] - mirroredQuarterArc[index][1]) < 1e-10).All(equal => equal);
            bool quarterArcBounded = quarterArc.Count >= 3 && mirroredSymmetrically &&
                Math.Abs(quarterArc[0][0] - 1.0) < 1e-12 && Math.Abs(quarterArc[0][1]) < 1e-12 &&
                Math.Abs(quarterArc[quarterArc.Count - 1][0]) < 1e-12 && Math.Abs(quarterArc[quarterArc.Count - 1][1] - 1.0) < 1e-12 &&
                maximumSagittaCm <= gostFilletChordErrorCm + 1e-12;
            Check("GOST-6211-CIRCULAR-FILLETS-TESSELLATE-SYMMETRICALLY-WITH-BOUNDED-SAGITTA",
                quarterArcBounded, ref pass, ref fail,
                "Mirrored circular fillets produce mirrored line points; each chord's true sagitta stays below the shared 0.005 mm tolerance.");
            Check("GOST-6211-COIL-PROFILE-USES-JIS-AXIAL-SECTION-CONVENTION",
                nativeTaperedBuilderSource.Contains("double radialSign = p.External ? -1.0 : 1.0;") &&
                nativeTaperedBuilderSource.Contains("startRadiusAtThreadCm + radialSign * depthOffsetCm") &&
                nativeTaperedBuilderSource.Contains("coil = def.Features.CoilFeatures.AddByPitchAndRevolution(") &&
                nativeTaperedBuilderSource.Contains("false, !p.RightHand, coilTaperAngle);") &&
                !nativeTaperedBuilderSource.Contains("GetConicalProfileRadiusCm("),
                ref pass, ref fail,
                "GOST uses the same axis-oriented cutter section and single native Coil.TaperAngle mechanism as the working JIS tapered-thread path; cone taper is not applied a second time to the profile.");
            Check("GOST-6211-ROUNDED-PROFILE-IS-LINE-ONLY-FOR-INVENTOR-COIL",
                nativeTaperedBuilderSource.Contains("TaperedThreadGeometryPolicy.ApproximateCircularArc(piece.P0, piece.Pm, piece.P1, gostFilletChordErrorCm)") &&
                nativeTaperedBuilderSource.Contains("sketch.SketchLines.AddByTwoPoints(startObject, endObject)") &&
                !nativeTaperedBuilderSource.Contains("sketch.SketchArcs.AddByThreePoints(startObject"),
                ref pass, ref fail,
                "The Coil profile contains only shared-endpoint sketch lines; four true GOST rounds are represented by short chords with 0.005 mm maximum radial deviation.");
            Check("GOST-6211-COIL-AND-RUNOUT-PROFILES-APPLY-TAPER-ONLY-ONCE",
                nativeTaperedBuilderSource.Contains("double radius = baseRadiusCm + radialSign * adjustedDepth;") &&
                nativeTaperedBuilderSource.Contains("double radiusAtVertex = baseRadiusCm + radialSign * depthAtVertex;") &&
                nativeTaperedBuilderSource.Contains("coneAppliedBy=Coil.TaperAngle") &&
                !nativeTaperedBuilderSource.Contains("GetConicalProfileRadiusCm("),
                ref pass, ref fail,
                "The primary Coil, segmented-Coil fallback and Loft sections follow the same JIS convention: a local axial section and one native tapered Coil angle.");
            Check("GOST-6211-CIRCULAR-ARC-TESSELLATOR-REJECTS-DEGENERATE-INPUT",
                ThrowsInvalidOperation(() => TaperedThreadGeometryPolicy.ApproximateCircularArc(
                    new[] { 0.0, 0.0 }, new[] { 0.5, 0.0 }, new[] { 1.0, 0.0 }, gostFilletChordErrorCm)),
                ref pass, ref fail,
                "Collinear/degenerate arc points must be rejected before producing an invalid Coil profile.");

            Check("RIBBON-COMMAND-IDENTITIES-ARE-STABLE-PER-LANGUAGE",
                RibbonCommandIdentityPolicy.CreateCommandName("ru") == "Helivanta.Cmd.Build.ru" &&
                RibbonCommandIdentityPolicy.CreateCommandName("RU") == "Helivanta.Cmd.Build.ru" &&
                RibbonCommandIdentityPolicy.CreateCommandName("en") == "Helivanta.Cmd.Build.en" &&
                RibbonCommandIdentityPolicy.EditCommandName("ru") == "Helivanta.Cmd.EditThread.ru" &&
                RibbonCommandIdentityPolicy.EditCommandName("en") == "Helivanta.Cmd.EditThread.en" &&
                RibbonCommandIdentityPolicy.CreateCommandName("de") == RibbonCommandIdentityPolicy.CreateCommandName("ru") &&
                ribbonCommandIdentityPolicySource.Contains("Helivanta.Cmd.Build.") &&
                ribbonCommandIdentityPolicySource.Contains("Helivanta.Cmd.EditThread."),
                ref pass, ref fail,
                "RU and EN must map to stable distinct Inventor ControlDefinition names, with unsupported values normalized to RU.");

            Check("ADDIN-DIALOG-LIFECYCLE-REUSES-ONLY-LIVE-HANDLE",
                AddInDialogLifecyclePolicy.ShouldReuse(true, false, true, true) &&
                !AddInDialogLifecyclePolicy.ShouldReuse(false, false, true, true) &&
                !AddInDialogLifecyclePolicy.ShouldReuse(true, true, true, true) &&
                !AddInDialogLifecyclePolicy.ShouldReuse(true, false, false, true) &&
                !AddInDialogLifecyclePolicy.ShouldReuse(true, false, true, false),
                ref pass, ref fail,
                "The ribbon command may reuse only an existing, non-disposed form with a created HWND that is actually visible.");
            object currentDialogReference = new object();
            object olderDialogReference = new object();
            Check("ADDIN-DIALOG-LIFECYCLE-CLEARS-ONLY-CURRENT-REFERENCE",
                AddInDialogLifecyclePolicy.ShouldClearCurrentReference(currentDialogReference, currentDialogReference) &&
                !AddInDialogLifecyclePolicy.ShouldClearCurrentReference(currentDialogReference, olderDialogReference) &&
                !AddInDialogLifecyclePolicy.ShouldClearCurrentReference(null, olderDialogReference) &&
                !AddInDialogLifecyclePolicy.ShouldClearCurrentReference(currentDialogReference, null),
                ref pass, ref fail,
                "A FormClosed or failed-Show callback may clear the add-in reference only when it still points to that exact dialog instance.");
            Check("BUILD-ROLLBACK-DISCARDS-SELECTED-COM-TARGETS-ONLY-AFTER-TRANSACTION",
                ThreadBuildTargetLifecyclePolicy.ShouldDiscardSelectionAfterFailure(true, true) &&
                !ThreadBuildTargetLifecyclePolicy.ShouldDiscardSelectionAfterFailure(true, false) &&
                !ThreadBuildTargetLifecyclePolicy.ShouldDiscardSelectionAfterFailure(false, true),
                ref pass, ref fail,
                "Preflight failures and cancelled invocations preserve a valid pick; a failed transactional build requires a fresh edge selection.");

            string addInLifecycleSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadProAddIn.cs"), Encoding.UTF8) : string.Empty;
            int onExecuteStart = addInLifecycleSource.IndexOf("private void OnExecute(NameValueMap context)", StringComparison.Ordinal);
            int onExecuteEnd = addInLifecycleSource.IndexOf("private sealed class SelectedMetadataEditTarget", onExecuteStart < 0 ? 0 : onExecuteStart, StringComparison.Ordinal);
            string onExecuteBody = onExecuteStart >= 0 && onExecuteEnd > onExecuteStart
                ? addInLifecycleSource.Substring(onExecuteStart, onExecuteEnd - onExecuteStart) : string.Empty;
            int reentrantGuardStart = onExecuteBody.IndexOf("if (_openingDialog)", StringComparison.Ordinal);
            int reentrantGuardOpen = reentrantGuardStart >= 0
                ? onExecuteBody.IndexOf("{", reentrantGuardStart, StringComparison.Ordinal) : -1;
            int reentrantGuardClose = reentrantGuardOpen >= 0
                ? onExecuteBody.IndexOf("}", reentrantGuardOpen, StringComparison.Ordinal) : -1;
            int reentrantGuardReturn = reentrantGuardOpen >= 0
                ? onExecuteBody.IndexOf("return;", reentrantGuardOpen, StringComparison.Ordinal) : -1;
            bool hasReentrantReturnGuard = reentrantGuardStart >= 0 &&
                reentrantGuardOpen > reentrantGuardStart && reentrantGuardClose > reentrantGuardOpen &&
                reentrantGuardReturn > reentrantGuardOpen && reentrantGuardReturn < reentrantGuardClose;

            int formClosedHandlerStart = onExecuteBody.IndexOf("createdDialog.FormClosed +=", StringComparison.Ordinal);
            int formClosedHandlerEnd = formClosedHandlerStart >= 0
                ? onExecuteBody.IndexOf("};", formClosedHandlerStart, StringComparison.Ordinal) : -1;
            string formClosedHandlerBody = formClosedHandlerStart >= 0 && formClosedHandlerEnd > formClosedHandlerStart
                ? onExecuteBody.Substring(formClosedHandlerStart, formClosedHandlerEnd - formClosedHandlerStart) : string.Empty;

            int initialShowPosition = onExecuteBody.IndexOf("createdDialog.Show(owner);", StringComparison.Ordinal);
            int failedOpenCatchStart = initialShowPosition >= 0
                ? onExecuteBody.IndexOf("catch (Exception ex)", initialShowPosition, StringComparison.Ordinal) : -1;
            int failedOpenCatchEnd = failedOpenCatchStart >= 0
                ? onExecuteBody.IndexOf("finally", failedOpenCatchStart, StringComparison.Ordinal) : -1;
            string failedOpenCleanupBody = failedOpenCatchStart >= 0 && failedOpenCatchEnd > failedOpenCatchStart
                ? onExecuteBody.Substring(failedOpenCatchStart, failedOpenCatchEnd - failedOpenCatchStart) : string.Empty;

            Check("ADDIN-RIBBON-REOPENS-STALE-DIALOG-AFTER-FAILED-OPEN",
                hasReentrantReturnGuard && onExecuteBody.Contains("Command.Create.reentrant-click-ignored") &&
                onExecuteBody.Contains("AddInDialogLifecyclePolicy.ShouldReuse(") &&
                onExecuteBody.Contains("existingDialog.IsHandleCreated, existingDialog.Visible") &&
                onExecuteBody.Contains("AddInDialogLifecyclePolicy.ShouldClearCurrentReference(_dlg, existingDialog)") &&
                onExecuteBody.Contains("existingDialog.Dispose()") &&
                initialShowPosition >= 0 && onExecuteBody.Contains("if (createdDialog.IsDisposed || !createdDialog.IsHandleCreated || !createdDialog.Visible)") &&
                formClosedHandlerBody.Contains("AddInDialogLifecyclePolicy.ShouldClearCurrentReference(_dlg, createdDialog)") &&
                failedOpenCleanupBody.Contains("AddInDialogLifecyclePolicy.ShouldClearCurrentReference(_dlg, createdDialog)") &&
                failedOpenCleanupBody.Contains("createdDialog.Dispose()"),
                ref pass, ref fail,
                "A stale/non-created modeless window must be discarded; a failed initial Show must clear and dispose its instance; late FormClosed events must not clear a newer dialog.");

            string edgeSelectorLifecycleSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "EdgeSelector.cs"), Encoding.UTF8) : string.Empty;
            int terminateStart = edgeSelectorLifecycleSource.IndexOf("private void OnInteractionTerminate()", StringComparison.Ordinal);
            int terminateEnd = edgeSelectorLifecycleSource.IndexOf("private static void ReleaseInteractionPair", terminateStart < 0 ? 0 : terminateStart, StringComparison.Ordinal);
            string terminateBody = terminateStart >= 0 && terminateEnd > terminateStart
                ? edgeSelectorLifecycleSource.Substring(terminateStart, terminateEnd - terminateStart) : string.Empty;
            int edgeTerminatePreSelectDetachPosition = terminateBody.IndexOf("se.OnPreSelect -=", StringComparison.Ordinal);
            int edgeTerminateSelectDetachPosition = terminateBody.IndexOf("se.OnSelect -=", StringComparison.Ordinal);
            int edgeTerminateInteractionDetachPosition = terminateBody.IndexOf("ie.OnTerminate -=", StringComparison.Ordinal);
            int edgeTerminateRetirePosition = terminateBody.IndexOf("_retiredInteractions.Add(", StringComparison.Ordinal);
            int edgeTerminateNotifyPosition = terminateBody.IndexOf("if (h != null) h();", StringComparison.Ordinal);
            int edgeSelectorDisposeStart = edgeSelectorLifecycleSource.IndexOf("public void Dispose()", StringComparison.Ordinal);
            int edgeSelectorDisposeEnd = edgeSelectorLifecycleSource.IndexOf("private void Say(", edgeSelectorDisposeStart < 0 ? 0 : edgeSelectorDisposeStart, StringComparison.Ordinal);
            string edgeSelectorDisposeBody = edgeSelectorDisposeStart >= 0 && edgeSelectorDisposeEnd > edgeSelectorDisposeStart
                ? edgeSelectorLifecycleSource.Substring(edgeSelectorDisposeStart, edgeSelectorDisposeEnd - edgeSelectorDisposeStart) : string.Empty;
            Check("EDGE-SELECTOR-TERMINATED-COM-EVENTS-ARE-RETIRED-AND-DRAINED-OUTSIDE-CALLBACK",
                edgeTerminatePreSelectDetachPosition >= 0 &&
                edgeTerminateSelectDetachPosition > edgeTerminatePreSelectDetachPosition &&
                edgeTerminateInteractionDetachPosition > edgeTerminateSelectDetachPosition &&
                edgeTerminateRetirePosition > edgeTerminateInteractionDetachPosition &&
                (edgeTerminateNotifyPosition < 0 || edgeTerminateNotifyPosition > edgeTerminateRetirePosition) &&
                !Regex.IsMatch(terminateBody, @"\b(?:Marshal\.)?FinalReleaseComObject\s*\(") &&
                !Regex.IsMatch(terminateBody, @"\bReleaseInteractionPair\s*\(") &&
                edgeSelectorLifecycleSource.Contains("DrainRetiredInteractions(\"EdgeSelector.Start\")") &&
                edgeSelectorLifecycleSource.Contains("DrainRetiredInteractions(\"EdgeSelector.Stop\")") &&
                edgeSelectorLifecycleSource.Contains("ReleaseInteractionPair(item.Selection, item.Interaction") &&
                edgeSelectorDisposeBody.Contains("Stop();"),
                ref pass, ref fail,
                "All three COM event handlers are detached before retirement; the callback queues (but never releases) its RCWs before notifying subscribers, while Start/Stop drain the queue and Dispose delegates to Stop.");

            int visualRefreshStart = edgeSelectorLifecycleSource.IndexOf("public void RefreshVisualSelection(", StringComparison.Ordinal);
            int highlightRefreshStart = edgeSelectorLifecycleSource.IndexOf("private void RefreshHighlight()", visualRefreshStart < 0 ? 0 : visualRefreshStart, StringComparison.Ordinal);
            int highlightCreateStart = edgeSelectorLifecycleSource.IndexOf("private void CreateHighlightSet(", highlightRefreshStart < 0 ? 0 : highlightRefreshStart, StringComparison.Ordinal);
            int highlightDeleteStart = edgeSelectorLifecycleSource.IndexOf("private void DeleteHighlight()", highlightCreateStart < 0 ? 0 : highlightCreateStart, StringComparison.Ordinal);
            int highlightDeleteEnd = edgeSelectorLifecycleSource.IndexOf("\n        }", highlightDeleteStart < 0 ? 0 : highlightDeleteStart, StringComparison.Ordinal);
            string visualHighlightMethods = visualRefreshStart >= 0 && highlightDeleteEnd > visualRefreshStart
                ? edgeSelectorLifecycleSource.Substring(visualRefreshStart, highlightDeleteEnd - visualRefreshStart) : string.Empty;
            string refreshHighlightMethod = highlightRefreshStart >= 0 && highlightCreateStart > highlightRefreshStart
                ? edgeSelectorLifecycleSource.Substring(highlightRefreshStart, highlightCreateStart - highlightRefreshStart) : string.Empty;
            string createHighlightMethod = highlightCreateStart >= 0 && highlightDeleteStart > highlightCreateStart
                ? edgeSelectorLifecycleSource.Substring(highlightCreateStart, highlightDeleteStart - highlightCreateStart) : string.Empty;
            bool visualHighlightFailuresAreDiagnosticOnly =
                edgeSelectorLifecycleSource.Contains("EdgeSelector.HasTargetsForActiveDocument") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.Start.CompareDocumentIdentity") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.Stop.DetachPreSelect") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.Stop.DetachSelect") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.Stop.DetachTerminate") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.RestartIfSameDocument") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.RestoreTargets.ReadActiveDocument") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.RestoreTargets.AnalyzeCurrentEdge") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.OnSelect.ResetSelections") &&
                edgeSelectorLifecycleSource.Contains("EdgeSelector.OnSelect.ActiveViewUpdate") &&
                !Regex.IsMatch(edgeSelectorLifecycleSource, @"catch\s*\{\s*\}") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshVisualSelection.ActiveViewUpdate") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshVisualSelection") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshHighlight.AddItem[") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshHighlight.Primary") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshHighlight.FallbackAddItem[") &&
                visualHighlightMethods.Contains("EdgeSelector.RefreshHighlight.Fallback") &&
                visualHighlightMethods.Contains("EdgeSelector.CreateHighlightSet") &&
                visualHighlightMethods.Contains("EdgeSelector.DeleteHighlight.Clear") &&
                visualHighlightMethods.Contains("EdgeSelector.DeleteHighlight.Delete") &&
                createHighlightMethod.Contains("created.Clear()") && createHighlightMethod.Contains("created.Delete()") &&
                refreshHighlightMethod.Contains("_edges.Count") &&
                !Regex.IsMatch(visualHighlightMethods, @"catch\s*\{\s*\}") &&
                !Regex.IsMatch(visualHighlightMethods, @"catch\s*\(\s*\)\s*\{");
            Check("EDGE-SELECTOR-HIGHLIGHT-COM-FAILURES-ARE-LOGGED-WITHOUT-INVALIDATING-LOGICAL-SELECTION",
                visualHighlightFailuresAreDiagnosticOnly, ref pass, ref fail,
                "Visual HighlightSet creation, clear/delete, edge insertion and redraw failures retain their best-effort behavior but must leave diagnostic context; partial HighlightSet creation is cleaned up and no visual failure clears the logical target list.");

            string runtimeAuditLifecycleSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "HelivantaRuntimeAuditAutomation.cs"), Encoding.UTF8) : string.Empty;
            Check("GOST-6211-LIVE-R-RC-AUDIT-USES-JIS-COIL-TAPER-CONVENTION",
                runtimeAuditLifecycleSource.Contains("Math.Tan(expectedCoilAngle) - (external ? 1.0 : -1.0) / 32.0") &&
                runtimeAuditLifecycleSource.Contains("ProfileMapping") &&
                runtimeAuditLifecycleSource.Contains("GOSTFilletMaxChordErrorMm") &&
                nativeTaperedBuilderSource.Contains("TaperedThreadGeometryPolicy.GostFilletMaxChordErrorCm * 10.0"),
                ref pass, ref fail,
                "Both real Inventor R/Rc builds must verify the signed 1:16 Coil taper, report the JIS-compatible profile strategy and record the GOST fillet chord tolerance.");

            string threadBuilderLifecycleSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadBuilder.cs"), Encoding.UTF8) : string.Empty;
            string threadDialogLifecycleSource = !string.IsNullOrWhiteSpace(sourceRoot)
                ? File.ReadAllText(Path.Combine(sourceRoot, "ThreadDialog.cs"), Encoding.UTF8) : string.Empty;
            Check("BUILD-ROLLBACK-FLAGS-TARGET-INVALIDATION-AND-DIALOG-CLEARS-STALE-EDGE-REFERENCES",
                threadBuilderLifecycleSource.Contains("LastBuildMayHaveInvalidatedTargets = false;") &&
                threadBuilderLifecycleSource.Contains("LastBuildMayHaveInvalidatedTargets = true;") &&
                threadDialogLifecycleSource.Contains("ThreadBuildTargetLifecyclePolicy.ShouldDiscardSelectionAfterFailure(") &&
                threadDialogLifecycleSource.Contains("_sel.Clear(false);") &&
                threadDialogLifecycleSource.Contains("build-failure-selection-invalidated"),
                ref pass, ref fail,
                "A failed build after StartTransaction discards selection COM references; validation failures before transaction creation preserve the selection.");

            Console.WriteLine("Helivanta — Metadata, Preview, Wall Thickness Safety, Sequential Native Inventor Thread Conversion, Nominal Diameter, Custom Pitch, Folder Selection, Technology Selection, Standard Selection, Chamfer Location Selection, Naming, Undo/Redo and Thread Edit Recovery Safety Test");
            Console.WriteLine("PASS: " + pass + "   FAIL: " + fail);
            Console.WriteLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return fail == 0 ? 0 : 2;
        }

        private static ThreadMetadataRecord CloneRecord(ThreadMetadataRecord record)
        {
            return JsonSerializer.Deserialize<ThreadMetadataRecord>(JsonSerializer.Serialize(record));
        }

        private static ThreadTargetSnapshot CloneTarget(ThreadTargetSnapshot target)
        {
            return new ThreadTargetSnapshot
            {
                Center = target.Center == null ? null : (double[])target.Center.Clone(),
                Axis = target.Axis == null ? null : (double[])target.Axis.Clone(),
                RadiusCm = target.RadiusCm,
                LengthCm = target.LengthCm,
                IsExternal = target.IsExternal,
                BodyName = target.BodyName,
                StartIsShoulder = target.StartIsShoulder,
                EndIsShoulder = target.EndIsShoulder
            };
        }

        private static ThreadMetadataRecord CreateOverlapTestRecord(string threadId, double centerZ,
            double lengthCm, string bodyName)
        {
            return new ThreadMetadataRecord
            {
                SchemaVersion = 1,
                ThreadId = threadId,
                ObjectCount = 1,
                Parameters = new ThreadParametersSnapshot
                {
                    SizeName = "M10", PitchMm = 1.0, NominalDiaMm = 10.0, Starts = 1,
                    External = true, FullLength = false, LengthMm = lengthCm * 10.0, OffsetMm = 0.0
                },
                Target = new ThreadTargetSnapshot
                {
                    Center = new[] { 0.0, 0.0, centerZ },
                    Axis = new[] { 0.0, 0.0, 1.0 },
                    RadiusCm = 0.5,
                    LengthCm = Math.Max(lengthCm, 1.0),
                    IsExternal = true,
                    BodyName = bodyName
                }
            };
        }

        private static IEnumerable<string> EnumerateFolderNamesThenThrow()
        {
            yield return "Thread — ISO · M10";
            throw new InvalidOperationException("Synthetic BrowserFolders enumeration failure.");
        }

        private static string ResolveSourceRoot()
        {
            // The metadata test is launched from build.ps1, but callers may run it
            // directly from the test project or its output directory. Walk upward
            // from both locations instead of relying on an undefined local path.
            string[] starts = { Environment.CurrentDirectory, AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                DirectoryInfo current = new DirectoryInfo(start);
                while (current != null)
                {
                    string candidate = Path.Combine(current.FullName, "src");
                    if (File.Exists(Path.Combine(candidate, "NativeThreadConversionService.cs")) &&
                        File.Exists(Path.Combine(candidate, "ThreadEditService.cs")))
                        return candidate;
                    current = current.Parent;
                }
            }
            return string.Empty;
        }

        private static bool ThrowsInvalidOperation(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return true; }
            catch { return false; }
            return false;
        }

        private static bool ThrowsArgumentOutOfRange(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { return true; }
            catch { return false; }
            return false;
        }

        private static bool ThreadBuilderRollbackCodeMappingIsStable(string source)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                source.Contains("threadBuildRollbackFailure.Code == HelivantaErrorCode.ThreadBuildRollbackUnverified") &&
                source.Contains("threadBuildSnapshotFailure.Code == HelivantaErrorCode.ThreadBuildRollbackSnapshotUnavailable") &&
                source.Contains("ThreadBuilder.Error.RollbackUnverified") &&
                source.Contains("ThreadBuilder.Error.RollbackSnapshotUnavailable");
        }

        private sealed class BrowserNodeFixture
        {
            private readonly Func<BrowserNativeFixture> _nativeReader;
            internal string DefinitionLabel { get; }
            internal bool IsBuiltInDefinition { get; }

            internal BrowserNodeFixture(string name, bool isEndMarker)
                : this(() => new BrowserNativeFixture(name, isEndMarker), name, false)
            {
            }

            internal BrowserNodeFixture(BrowserNativeFixture native)
                : this(() => native, native?.Name ?? string.Empty, false)
            {
            }

            internal BrowserNodeFixture(Func<BrowserNativeFixture> nativeReader)
                : this(nativeReader, string.Empty, false)
            {
            }

            internal BrowserNodeFixture(string definitionLabel, bool isBuiltInDefinition, BrowserNativeFixture native)
                : this(() => native, definitionLabel, isBuiltInDefinition)
            {
            }

            private BrowserNodeFixture(Func<BrowserNativeFixture> nativeReader, string definitionLabel, bool isBuiltInDefinition)
            {
                _nativeReader = nativeReader ?? throw new ArgumentNullException(nameof(nativeReader));
                DefinitionLabel = definitionLabel ?? string.Empty;
                IsBuiltInDefinition = isBuiltInDefinition;
            }

            internal BrowserNativeFixture ReadNativeObject() => _nativeReader();
        }

        private sealed class BrowserNativeFixture
        {
            internal string Name { get; }
            internal bool IsEndMarker { get; }
            internal BrowserNativeFixture(string name, bool isEndMarker)
            {
                Name = name;
                IsEndMarker = isEndMarker;
            }
        }

        private static IEnumerable<BrowserNodeFixture> EnumerateBrowserNodesThenThrow()
        {
            yield return new BrowserNodeFixture("FeatureBeforeFailure", false);
            throw new InvalidOperationException("Synthetic BrowserNodes enumeration failure after a plausible anchor.");
        }

        private sealed class BodyNameFixture
        {
            internal string Name { get; }
            internal BodyNameFixture(string name) { Name = name; }
        }

        private static IEnumerable<BodyNameFixture> EnumerateBodyNamesThenThrow()
        {
            yield return new BodyNameFixture("Solid1");
            throw new InvalidOperationException("Synthetic SurfaceBodies enumeration failure after a plausible match.");
        }

        private static void Check(string id, bool condition, ref int pass, ref int fail, string description)
        {
            if (condition)
            {
                pass++;
                Console.WriteLine("[PASS] " + id + " — " + description);
            }
            else
            {
                fail++;
                Console.WriteLine("[FAIL] " + id + " — " + description);
            }
        }
    }
}
