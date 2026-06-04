#!/usr/bin/env python3
"""Export RAWR's OpenCLIP image encoder to subject_image_encoder.onnx.

Run from the RAWR repository root after installing:

    pip install torch torchvision open_clip_torch onnx

Example:

    python tools/export_subject_image_encoder.py `
      --model ViT-B-16 `
      --pretrained datacomp_xl_s13b_b90k `
      --output src/Rawr.App/models/subject_image_encoder.onnx
"""

from pathlib import Path
import argparse
import sys

import torch

# Some Windows/Python/torchvision combinations fail importing torchvision because
# torchvision::nms is not registered. This define is harmless when not needed.
try:
    lib = torch.library.Library("torchvision", "DEF")
    lib.define("nms(Tensor dets, Tensor scores, float iou_threshold) -> Tensor")
except Exception:
    pass

import open_clip


class RawrImageEncoder(torch.nn.Module):
    def __init__(self, clip_model: torch.nn.Module):
        super().__init__()
        self.visual = clip_model.visual

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        # Input:
        #   image: NCHW float32, normally [1, 3, 224, 224] for ViT-B-16.
        #
        # Output:
        #   A single normalized 1-D embedding vector.
        #
        # RAWR classifies one image at a time, so this exports a fixed batch=1
        # encoder and removes the batch dimension from the output.
        embedding = self.visual(image)
        embedding = embedding / embedding.norm(dim=-1, keepdim=True).clamp_min(1e-12)
        return embedding[0]


def disable_fast_attention_paths() -> None:
    """Disable PyTorch attention fast paths that do not export cleanly to ONNX."""

    # This is the important fix for:
    #   UnsupportedOperatorError:
    #   Exporting the operator 'aten::_native_multi_head_attention' ...
    #
    # Without this, PyTorch may use its optimized native MHA implementation,
    # which the legacy ONNX exporter cannot handle.
    if hasattr(torch.backends, "mha"):
        try:
            torch.backends.mha.set_fastpath_enabled(False)
            print("Disabled torch.backends.mha fast path.")
        except Exception as exc:
            print(f"Warning: could not disable MHA fast path: {exc}")

    # Keep CPU execution predictable.
    torch.set_grad_enabled(False)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export an OpenCLIP image encoder to ONNX for RAWR."
    )

    parser.add_argument(
        "--model",
        default="ViT-B-16",
        help="OpenCLIP model name. Default: ViT-B-16",
    )

    parser.add_argument(
        "--pretrained",
        default="datacomp_xl_s13b_b90k",
        help="OpenCLIP pretrained tag. Default: datacomp_xl_s13b_b90k",
    )

    parser.add_argument(
        "--output",
        default="src/Rawr.App/models/subject_image_encoder.onnx",
        help="Output ONNX path.",
    )

    parser.add_argument(
        "--opset",
        type=int,
        default=17,
        help="ONNX opset version. Default: 17",
    )

    return parser.parse_args()


def main() -> None:
    args = parse_args()

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    disable_fast_attention_paths()

    print(f"Loading OpenCLIP {args.model} / {args.pretrained}...")

    try:
        model, _, _ = open_clip.create_model_and_transforms(
            args.model,
            pretrained=args.pretrained,
            device="cpu",
        )
    except Exception as exc:
        print()
        print("Failed to load OpenCLIP model.")
        print("This is usually a download/cache/network/model-name problem.")
        print(f"Error: {exc}")
        sys.exit(1)

    model.eval()

    # Make absolutely sure all parameters are frozen.
    for parameter in model.parameters():
        parameter.requires_grad_(False)

    encoder = RawrImageEncoder(model).eval()

    image_size = model.visual.image_size
    if isinstance(image_size, tuple):
        height, width = image_size
    else:
        height = width = int(image_size)

    dummy = torch.randn(1, 3, height, width, dtype=torch.float32)

    print(f"Exporting fixed NCHW input [1, 3, {height}, {width}] to:")
    print(f"  {output}")

    try:
        with torch.inference_mode():
            torch.onnx.export(
                encoder,
                dummy,
                str(output),
                input_names=["image"],
                output_names=["embedding"],
                opset_version=args.opset,
                do_constant_folding=True,
                dynamo=False,
            )
    except Exception as exc:
        print()
        print("ONNX export failed.")
        print()
        print("Most likely cause:")
        print("  Your installed PyTorch version still exported an unsupported attention operator.")
        print()
        print("Try this stable fallback inside your activated venv:")
        print("  python -m pip uninstall torch torchvision -y")
        print("  python -m pip install torch==2.5.1 torchvision==0.20.1")
        print()
        print("Then rerun this script.")
        print()
        print(f"Original error: {exc}")
        sys.exit(1)

    size_mb = output.stat().st_size / 1024 / 1024
    print()
    print(f"Done: {output} ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()