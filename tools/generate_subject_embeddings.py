"""
Generate src/Rawr.App/models/subject_tags.json for RAWR's subject classifier.

Run this once, offline, with the same CLIP variant whose image encoder you
exported to ONNX. The script averages the embeddings of several prompt
variants per category, L2-normalises the result, and writes a JSON file
that the runtime loads at startup.

Requirements:
    pip install open_clip_torch torch

Usage:
    python tools/generate_subject_embeddings.py \
        --model ViT-B-16 \
        --pretrained datacomp_xl_s13b_b90k \
        --output src/Rawr.App/models/subject_tags.json

The category names must match the SubjectTag enum in
src/Rawr.Core/Models/SubjectTag.cs (case-insensitive). Add a new entry there
first, then re-run this script with the matching name.
"""

import argparse
import json
import sys
from pathlib import Path

# Tag set + prompt variants. The runtime averages the text embeddings across
# variants per tag, then L2-normalises — small but reliable boost over a
# single prompt. Keep the names in sync with SubjectTag in Rawr.Core (the names
# must match the enum, case-insensitive) and the grouping with SubjectTaxonomy.
#
# Both group roots (Animal, Vehicle, Nature) and their leaves (Dog, Cat, ...)
# get their own embedding and are scored independently; the runtime then rolls
# any leaf hit up into its group (SubjectTaxonomy.ApplyGroupRollup), so a group
# embedding only needs to catch members that don't match a specific leaf.
TAG_PROMPTS = {
    # ── Standalone categories ──
    "Person": [
        "a photo of a person",
        "a portrait of a person",
        "a photo of people",
        "a candid photo of someone",
    ],
    "Food": [
        "a photo of food",
        "a photo of a meal on a plate",
        "a close-up photo of a dish",
        "a photo of a restaurant meal",
    ],
    "Architecture": [
        "a photo of a building",
        "a photo of architecture",
        "a photo of a cityscape",
        "a photo of a city street",
        "a photo of the interior of a building",
    ],

    # ── Animal group + leaves ──
    "Animal": [
        "a photo of an animal",
        "a photo of a pet",
        "a wildlife photo",
        "a photo of a creature",
    ],
    "Dog": [
        "a photo of a dog",
        "a photo of a puppy",
    ],
    "Cat": [
        "a photo of a cat",
        "a photo of a kitten",
    ],
    "Bird": [
        "a photo of a bird",
        "a photo of a bird in flight",
    ],
    "Horse": [
        "a photo of a horse",
        "a photo of horses in a field",
    ],
    "Wildlife": [
        "a wildlife photo",
        "a photo of a wild animal",
        "a photo of a deer",
        "a photo of a fox",
    ],

    # ── Vehicle group + leaves ──
    "Vehicle": [
        "a photo of a vehicle",
        "a photo of a motor vehicle",
        "a photo of a mode of transport",
    ],
    "Car": [
        "a photo of a car",
        "a photo of an automobile",
    ],
    "Plane": [
        "a photo of an airplane",
        "a photo of a plane in the sky",
    ],
    "Bike": [
        "a photo of a bicycle",
        "a photo of a motorcycle",
    ],
    "Boat": [
        "a photo of a boat",
        "a photo of a ship",
    ],
    "Train": [
        "a photo of a train",
        "a photo of a train on the tracks",
    ],

    # ── Nature group (was Landscape) + leaves ──
    "Nature": [
        "a landscape photo",
        "a photo of nature",
        "a photo of natural scenery",
        "a wide outdoor scenery photo",
        "a photo of the outdoors",
    ],
    "Mountain": [
        "a photo of a mountain",
        "a photo of mountains",
    ],
    "Forest": [
        "a photo of a forest",
        "a photo of trees in a forest",
    ],
    "Water": [
        "a photo of a lake",
        "a photo of the sea",
        "a photo of a river",
        "a photo of the ocean",
    ],
    "Beach": [
        "a photo of a beach",
        "a photo of a sandy beach by the sea",
    ],
    "Sky": [
        "a photo of the sky",
        "a photo of a sunset",
        "a photo of clouds in the sky",
    ],
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", default="ViT-B-16",
                        help="OpenCLIP model architecture (must match the ONNX image encoder).")
    parser.add_argument("--pretrained", default="datacomp_xl_s13b_b90k",
                        help="OpenCLIP pretrained tag.")
    parser.add_argument("--output", default="src/Rawr.App/models/subject_tags.json",
                        help="Output JSON path.")
    args = parser.parse_args()

    try:
        import open_clip
        import torch
    except ImportError as e:
        print(f"Missing dependency: {e}. Run: pip install open_clip_torch torch", file=sys.stderr)
        return 1

    print(f"Loading {args.model} ({args.pretrained})...")
    model, _, _ = open_clip.create_model_and_transforms(args.model, pretrained=args.pretrained)
    tokenizer = open_clip.get_tokenizer(args.model)
    model.eval()

    tags_out = []
    with torch.no_grad():
        for name, prompts in TAG_PROMPTS.items():
            tokens = tokenizer(prompts)
            embeds = model.encode_text(tokens).float()
            # L2-normalise each prompt, average, then re-normalise.
            embeds = embeds / embeds.norm(dim=-1, keepdim=True)
            mean = embeds.mean(dim=0)
            mean = mean / mean.norm()
            tags_out.append({
                "name": name,
                "prompts": prompts,
                "embedding": mean.tolist(),
            })
            print(f"  {name}: {len(prompts)} prompts, dim={mean.shape[0]}")

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps({
        "model": f"{args.model}/{args.pretrained}",
        "embed_dim": len(tags_out[0]["embedding"]),
        "tags": tags_out,
    }, indent=2))
    print(f"Wrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
