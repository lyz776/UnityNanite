"""Launch/connect to a RenderDoc target and trigger one deterministic capture.

Run only through qrenderdoc's embedded Python runtime.  The PowerShell wrapper in
this directory configures the environment and invokes this script.
"""

from __future__ import print_function

import datetime
import json
import os
import subprocess
import sys
import time
import traceback

import renderdoc as rd


def _environment_int(name, default):
    text = os.environ.get(name, "").strip()
    return default if not text else int(text)


def _environment_float(name, default):
    text = os.environ.get(name, "").strip()
    return default if not text else float(text)


def _status_succeeded(status):
    if status == rd.ResultCode.Succeeded:
        return True
    try:
        return status.code == rd.ResultCode.Succeeded
    except Exception:
        return False


def _write_result(path, value):
    if not path:
        return
    path = os.path.abspath(path)
    parent = os.path.dirname(path)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    with open(path, "w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=False)
        stream.write("\n")


def _receive_until(target, deadline, wanted_type=None):
    while target.Connected() and time.monotonic() < deadline:
        message = target.ReceiveMessage(None)
        if wanted_type is not None and message.type == wanted_type:
            return message
    return None


def _connect_target(ident, deadline):
    while time.monotonic() < deadline:
        target = rd.CreateTargetControl(
            "", ident, "UnityNanite GPU timing capture", True
        )
        if target is not None and target.Connected():
            return target
        if target is not None:
            target.Shutdown()
        time.sleep(0.1)
    return None


def _query_nvidia_gpus():
    try:
        output = subprocess.check_output([
            "nvidia-smi",
            "--query-gpu=index,name,pci.device_id,driver_version",
            "--format=csv,noheader,nounits",
        ], universal_newlines=True, timeout=5.0)
    except Exception as error:
        return {"error": str(error), "gpus": []}

    gpus = []
    for line in output.splitlines():
        columns = [column.strip() for column in line.split(",")]
        if len(columns) < 4:
            continue
        gpus.append({
            "index": columns[0],
            "name": columns[1],
            "pciDeviceId": columns[2],
            "driverVersion": columns[3],
        })
    return {"error": None, "gpus": gpus}


def capture_one_frame():
    result_path = os.environ.get("GI_RDC_CAPTURE_RESULT", "").strip()
    timeout_seconds = _environment_float("GI_RDC_TIMEOUT_SECONDS", 90.0)
    warmup_seconds = _environment_float("GI_RDC_WARMUP_SECONDS", 5.0)
    queued_frame_text = os.environ.get("GI_RDC_FRAME", "").strip()
    queued_frame = int(queued_frame_text) if queued_frame_text else None
    capture_template = os.environ.get("GI_RDC_CAPTURE_TEMPLATE", "").strip()
    target_ident = _environment_int("GI_RDC_TARGET_IDENT", 0)
    launched = False

    if timeout_seconds <= 0.0:
        raise RuntimeError("GI_RDC_TIMEOUT_SECONDS must be positive")
    if warmup_seconds < 0.0:
        raise RuntimeError("GI_RDC_WARMUP_SECONDS cannot be negative")

    if target_ident == 0:
        executable = os.environ.get("GI_RDC_EXECUTABLE", "").strip()
        arguments = os.environ.get("GI_RDC_ARGUMENTS", "")
        working_directory = os.environ.get("GI_RDC_WORKING_DIRECTORY", "").strip()
        if not executable or not os.path.isfile(executable):
            raise RuntimeError("GI_RDC_EXECUTABLE is not a file: " + executable)
        if not working_directory:
            working_directory = os.path.dirname(os.path.abspath(executable))
        if not os.path.isdir(working_directory):
            raise RuntimeError(
                "GI_RDC_WORKING_DIRECTORY is not a directory: "
                + working_directory
            )
        if capture_template:
            capture_template = os.path.abspath(capture_template)
            capture_parent = os.path.dirname(capture_template)
            if capture_parent and not os.path.isdir(capture_parent):
                os.makedirs(capture_parent)

        options = rd.CaptureOptions()
        options.allowVSync = (
            os.environ.get("GI_RDC_ALLOW_VSYNC", "0").strip() == "1"
        )
        launch = rd.ExecuteAndInject(
            os.path.abspath(executable),
            os.path.abspath(working_directory),
            arguments,
            [],
            capture_template,
            options,
            False,
        )
        if not _status_succeeded(launch.result):
            raise RuntimeError(
                "RenderDoc ExecuteAndInject failed: " + str(launch.result)
            )
        target_ident = int(launch.ident)
        launched = True

    deadline = time.monotonic() + timeout_seconds
    target = _connect_target(target_ident, deadline)
    if target is None:
        raise RuntimeError(
            "Could not connect to RenderDoc target ident %d" % target_ident
        )

    try:
        if queued_frame is not None:
            if queued_frame < 0:
                raise RuntimeError("GI_RDC_FRAME cannot be negative")
            # Frame 0 starts at D3D device creation.  Queue immediately so this
            # is repeatable across runs and independent of host-side sleeps.
            target.QueueCapture(queued_frame, 1)
        else:
            warmup_deadline = min(deadline, time.monotonic() + warmup_seconds)
            _receive_until(target, warmup_deadline)
            if not target.Connected():
                raise RuntimeError("RenderDoc target exited during warmup")
            target.TriggerCapture(1)

        message = _receive_until(
            target,
            deadline,
            rd.TargetControlMessageType.NewCapture,
        )
        if message is None:
            raise RuntimeError(
                "Timed out waiting for RenderDoc NewCapture notification"
            )

        capture_path = os.path.abspath(message.newCapture.path)
        # NewCapture is sent after serialisation, but allow filesystem metadata
        # to catch up before the exporter starts in a second process.
        file_deadline = min(deadline, time.monotonic() + 5.0)
        while not os.path.isfile(capture_path) and time.monotonic() < file_deadline:
            time.sleep(0.05)
        if not os.path.isfile(capture_path):
            raise RuntimeError(
                "RenderDoc reported a capture that is not present: "
                + capture_path
            )

        result = {
            "schemaVersion": 1,
            "renderDocVersion": rd.GetVersionString(),
            "renderDocCommit": rd.GetCommitHash(),
            "capturedAtUtc": datetime.datetime.utcnow().isoformat() + "Z",
            "launchedTarget": launched,
            "targetIdent": target_ident,
            "capture": capture_path,
            "frameNumber": int(message.newCapture.frameNumber),
            "graphicsAPI": str(message.newCapture.api),
            "queuedFrame": queued_frame,
            "warmupSeconds": None if queued_frame is not None else warmup_seconds,
            "nvidiaSmi": _query_nvidia_gpus(),
        }
        _write_result(result_path, result)
        print(
            "Captured %s frame %d to %s"
            % (result["graphicsAPI"], result["frameNumber"], capture_path)
        )
        return result
    finally:
        target.Shutdown()


def main():
    result_path = os.environ.get("GI_RDC_CAPTURE_RESULT", "").strip()
    try:
        capture_one_frame()
        return 0
    except Exception as error:
        failure = {
            "schemaVersion": 1,
            "error": str(error),
            "traceback": traceback.format_exc(),
        }
        try:
            _write_result(result_path, failure)
        except Exception:
            pass
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
