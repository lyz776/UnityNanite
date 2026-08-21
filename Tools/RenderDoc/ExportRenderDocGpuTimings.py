"""Export replayed GPU event durations from one RenderDoc capture.

This script is meant to run inside qrenderdoc's embedded Python 3.6 runtime:

    qrenderdoc.exe --python ExportRenderDocGpuTimings.py

Configuration is passed through environment variables because qrenderdoc owns its
command-line parser:

    GI_RDC_CAPTURE          required .rdc path
    GI_RDC_OUTPUT_JSON      optional output path (defaults beside the capture)
    GI_RDC_OUTPUT_CSV       optional marker CSV path
    GI_RDC_MARKER_PREFIXES  semicolon-separated substrings (default RealtimeGI/)

The exported values come from GPUCounter.EventGPUDuration.  On D3D12 RenderDoc
implements this with two GPU timestamp queries around each replayed action and
reports seconds.  Marker totals are unique sums of the timed leaf actions below
the marker, so nested markers never double-count within one row.
"""

from __future__ import print_function

import csv
import json
import math
import os
import sys
import traceback

import renderdoc as rd


SCHEMA_VERSION = 1


def _required_environment(name):
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError("Missing required environment variable " + name)
    return value


def _status_succeeded(status):
    if status == rd.ResultCode.Succeeded:
        return True
    try:
        return status.code == rd.ResultCode.Succeeded
    except Exception:
        return False


def _action_name(action, structured_file):
    try:
        return action.GetName(structured_file)
    except Exception:
        try:
            return action.customName
        except Exception:
            return "Event " + str(action.eventId)


def _matches_marker(name, prefixes):
    folded = name.lower()
    return any(prefix.lower() in folded for prefix in prefixes)


def _sum_duration(event_ids, durations_ms):
    return sum(durations_ms[event_id] for event_id in event_ids)


def _write_json(path, value):
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    with open(path, "w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=False)
        stream.write("\n")


def _write_csv(path, markers):
    if not path:
        return
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    with open(path, "w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow([
            "eventId",
            "firstTimedEventId",
            "lastTimedEventId",
            "depth",
            "marker",
            "inclusiveGpuMs",
            "exclusiveGpuMs",
            "timedActionCount",
            "exclusiveTimedActionCount",
            "path",
        ])
        for marker in markers:
            writer.writerow([
                marker["eventId"],
                marker["firstTimedEventId"],
                marker["lastTimedEventId"],
                marker["depth"],
                marker["name"],
                "%.6f" % marker["inclusiveGpuMs"],
                "%.6f" % marker["exclusiveGpuMs"],
                marker["timedActionCount"],
                marker["exclusiveTimedActionCount"],
                marker["path"],
            ])


def export_capture(capture_path, output_json, output_csv, prefixes):
    capture_path = os.path.abspath(capture_path)
    if not os.path.isfile(capture_path):
        raise RuntimeError("Capture does not exist: " + capture_path)

    cap = rd.OpenCaptureFile()
    controller = None
    try:
        opened = cap.OpenFile(capture_path, "", None)
        if not _status_succeeded(opened):
            raise RuntimeError("Could not open capture: " + str(opened))
        if not cap.LocalReplaySupport():
            raise RuntimeError("Capture cannot be replayed locally")

        available_gpus = []
        for gpu in cap.GetAvailableGPUs():
            available_gpus.append({
                "vendor": str(gpu.vendor),
                "deviceId": int(gpu.deviceID),
                "driver": gpu.driver,
                "name": gpu.name,
                "apis": [str(api) for api in gpu.apis],
            })

        opened, controller = cap.OpenCapture(rd.ReplayOptions(), None)
        if not _status_succeeded(opened):
            raise RuntimeError("Could not initialise replay: " + str(opened))

        available = controller.EnumerateCounters()
        duration_counter = rd.GPUCounter.EventGPUDuration
        if duration_counter not in available:
            raise RuntimeError(
                "Replay driver does not expose GPUCounter.EventGPUDuration"
            )

        description = controller.DescribeCounter(duration_counter)
        # The generic D3D12 counter must be a float64 value in seconds.  Refuse
        # to silently emit values if a future API changes that contract.
        if description.resultByteWidth != 8 or description.resultType != rd.CompType.Float:
            raise RuntimeError(
                "Unexpected GPU duration representation: width=%s type=%s"
                % (description.resultByteWidth, description.resultType)
            )
        if description.unit != rd.CounterUnit.Seconds:
            raise RuntimeError(
                "Unexpected GPU duration unit: " + str(description.unit)
            )

        counter_results = controller.FetchCounters([duration_counter])
        durations_ms = {}
        duplicate_event_ids = []
        for result in counter_results:
            if result.counter != duration_counter:
                continue
            value_ms = float(result.value.d) * 1000.0
            if not math.isfinite(value_ms) or value_ms < 0.0:
                raise RuntimeError(
                    "Invalid GPU duration for EID %s: %s"
                    % (result.eventId, value_ms)
                )
            event_id = int(result.eventId)
            if event_id in durations_ms:
                duplicate_event_ids.append(event_id)
            durations_ms[event_id] = value_ms

        if not durations_ms:
            raise RuntimeError(
                "EventGPUDuration returned no timestamp results; the capture is not "
                "usable for GPU pass timing"
            )

        structured_file = controller.GetStructuredFile()
        action_records = {}
        markers_internal = []
        group_candidates = []

        def visit(action, ancestor_names, depth):
            name = _action_name(action, structured_file)
            event_id = int(action.eventId)
            path_names = ancestor_names + [name]
            child_leaf_ids = set()
            top_child_markers = []

            for child in action.children:
                child_ids, child_top_markers = visit(
                    child, path_names, depth + 1
                )
                child_leaf_ids.update(child_ids)
                top_child_markers.extend(child_top_markers)

            # RenderDoc may expose a timed group as well as timed descendants.
            # Use the deepest timed actions only, otherwise a group and its
            # children would represent the same GPU work twice.
            if child_leaf_ids:
                leaf_ids = child_leaf_ids
            elif event_id in durations_ms:
                leaf_ids = {event_id}
            else:
                leaf_ids = set()

            action_records[event_id] = {
                "eventId": event_id,
                "name": name,
                "flags": str(action.flags),
                "depth": depth,
                "path": " > ".join(path_names),
            }

            if len(action.children) > 0:
                group_candidates.append({
                    "eventId": event_id,
                    "name": name,
                    "flags": str(action.flags),
                    "depth": depth,
                    "timedActionCount": len(leaf_ids),
                    "gpuMs": _sum_duration(leaf_ids, durations_ms),
                    "path": " > ".join(path_names),
                })

            if _matches_marker(name, prefixes):
                nested_ids = set()
                for child_marker in top_child_markers:
                    nested_ids.update(child_marker["_inclusiveIds"])
                marker = {
                    "eventId": event_id,
                    "name": name,
                    "depth": depth,
                    "path": " > ".join(path_names),
                    "_inclusiveIds": set(leaf_ids),
                    "_exclusiveIds": set(leaf_ids).difference(nested_ids),
                }
                markers_internal.append(marker)
                return leaf_ids, [marker]

            return leaf_ids, top_child_markers

        all_frame_leaf_ids = set()
        top_markers = []
        for root in controller.GetRootActions():
            root_ids, root_markers = visit(root, [], 0)
            all_frame_leaf_ids.update(root_ids)
            top_markers.extend(root_markers)

        gi_event_ids = set()
        for marker in top_markers:
            gi_event_ids.update(marker["_inclusiveIds"])

        markers = []
        for marker in markers_internal:
            inclusive_ids = marker.pop("_inclusiveIds")
            exclusive_ids = marker.pop("_exclusiveIds")
            serialised = dict(marker)
            serialised.update({
                "firstTimedEventId": min(inclusive_ids) if inclusive_ids else None,
                "lastTimedEventId": max(inclusive_ids) if inclusive_ids else None,
                "timedActionCount": len(inclusive_ids),
                "exclusiveTimedActionCount": len(exclusive_ids),
                "inclusiveGpuMs": _sum_duration(inclusive_ids, durations_ms),
                "exclusiveGpuMs": _sum_duration(exclusive_ids, durations_ms),
            })
            markers.append(serialised)

        markers.sort(key=lambda item: (
            item["firstTimedEventId"] if item["firstTimedEventId"] is not None else item["eventId"],
            item["depth"],
            item["eventId"],
        ))

        gi_actions = []
        for event_id in sorted(gi_event_ids):
            record = dict(action_records.get(event_id, {
                "eventId": event_id,
                "name": "Unknown timed action",
                "flags": "",
                "depth": -1,
                "path": "",
            }))
            record["gpuMs"] = durations_ms[event_id]
            gi_actions.append(record)

        timed_action_samples = []
        for event_id in sorted(durations_ms)[:256]:
            record = dict(action_records.get(event_id, {
                "eventId": event_id,
                "name": "Unknown timed action",
                "flags": "",
                "depth": -1,
                "path": "",
            }))
            record["gpuMs"] = durations_ms[event_id]
            timed_action_samples.append(record)

        properties = controller.GetAPIProperties()
        driver_information = rd.GetDriverInformation(properties.pipelineType)
        warnings = [
            "Times are GPU timestamp durations measured while RenderDoc replays the captured frame.",
            "measuredActionSumMs is a sum of unique timed actions, not a multi-queue wall-clock frame span.",
            "Marker sums can exclude un-timestamped barriers or queue idle time; use them as pass work timings.",
        ]
        if not markers:
            warnings.append(
                "No action name matched marker prefixes: " + ";".join(prefixes)
            )
        elif not gi_event_ids:
            warnings.append("Matching markers contained no timestamped GPU actions.")
        if duplicate_event_ids:
            warnings.append(
                "Duplicate duration results were returned for event IDs: "
                + ",".join(str(value) for value in sorted(set(duplicate_event_ids)))
            )

        export = {
            "schemaVersion": SCHEMA_VERSION,
            "renderDocVersion": rd.GetVersionString(),
            "renderDocCommit": rd.GetCommitHash(),
            "capture": capture_path,
            "graphicsAPI": str(properties.pipelineType),
            "replay": {
                "localRenderer": str(properties.localRenderer),
                "vendor": str(properties.vendor),
                "remote": bool(properties.remoteReplay),
                "degraded": bool(properties.degraded),
                "driverVendor": str(driver_information.vendor),
                "driverVersion": driver_information.version,
                "availableGPUs": available_gpus,
            },
            "markerPrefixes": prefixes,
            "counter": {
                "name": description.name,
                "description": description.description,
                "unit": str(description.unit),
                "resultType": str(description.resultType),
                "resultByteWidth": int(description.resultByteWidth),
            },
            "durationResultCount": len(durations_ms),
            "measuredFrameActionCount": len(all_frame_leaf_ids),
            "measuredFrameActionSumMs": _sum_duration(
                all_frame_leaf_ids, durations_ms
            ),
            "matchedMarkerCount": len(markers),
            "matchedActionCount": len(gi_event_ids),
            "matchedActionSumMs": _sum_duration(gi_event_ids, durations_ms),
            "markers": markers,
            "matchedActions": gi_actions,
            "groupCandidates": group_candidates,
            "timedActionSamples": timed_action_samples,
            "warnings": warnings,
        }

        _write_json(output_json, export)
        _write_csv(output_csv, markers)
        return export
    finally:
        if controller is not None:
            controller.Shutdown()
        cap.Shutdown()


def main():
    capture_path = _required_environment("GI_RDC_CAPTURE")
    output_json = os.environ.get("GI_RDC_OUTPUT_JSON", "").strip()
    if not output_json:
        output_json = os.path.splitext(capture_path)[0] + ".gpu.json"
    output_csv = os.environ.get("GI_RDC_OUTPUT_CSV", "").strip()
    if not output_csv:
        output_csv = os.path.splitext(output_json)[0] + ".csv"
    prefixes = [
        item.strip()
        for item in os.environ.get(
            "GI_RDC_MARKER_PREFIXES", "RealtimeGI/"
        ).split(";")
        if item.strip()
    ]
    if not prefixes:
        raise RuntimeError("GI_RDC_MARKER_PREFIXES did not contain a marker")

    export = export_capture(capture_path, output_json, output_csv, prefixes)
    print(
        "RenderDoc GPU timing: API=%s results=%d matched=%d %.3f ms"
        % (
            export["graphicsAPI"],
            export["durationResultCount"],
            export["matchedActionCount"],
            export["matchedActionSumMs"],
        )
    )
    print("JSON: " + os.path.abspath(output_json))
    print("CSV: " + os.path.abspath(output_csv))
    return 0


if __name__ == "__main__":
    status_path = os.environ.get("GI_RDC_EXPORT_RESULT", "").strip()
    run_token = os.environ.get("GI_RDC_RUN_TOKEN", "").strip()
    try:
        exit_code = main()
        if status_path:
            _write_json(status_path, {
                "schemaVersion": 1,
                "success": True,
                "runToken": run_token,
                "outputJson": os.path.abspath(
                    os.environ.get("GI_RDC_OUTPUT_JSON", "")
                ),
            })
        sys.exit(exit_code)
    except SystemExit:
        raise
    except Exception:
        formatted_traceback = traceback.format_exc()
        if status_path:
            try:
                _write_json(status_path, {
                    "schemaVersion": 1,
                    "success": False,
                    "runToken": run_token,
                    "error": formatted_traceback.splitlines()[-1],
                    "traceback": formatted_traceback,
                })
            except Exception:
                pass
        print(formatted_traceback)
        sys.exit(1)
