"""
Generate src/Rawr.App/models/subject_tags.json for RAWR's subject classifier.

Run this once, offline, with the same CLIP variant whose image encoder you
exported to ONNX. The script ensembles each category over many prompt variants
(noun phrases × templates, see TEMPLATES / TAG_TERMS), averages and
L2-normalises the result, and writes a JSON file the runtime loads at startup.
It also emits a "background" anchor entry (BACKGROUND_PROMPTS) that the runtime
uses in its softmax to absorb none-of-the-above probability mass.

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

# Prompt-template ensemble. CLIP zero-shot is meaningfully more robust when each
# category is averaged over many phrasings instead of one or two — the well-worn
# OpenCLIP "prompt ensembling" trick. We cross every category's noun phrase(s)
# (TAG_TERMS below) with these templates, embed them all, and average. Templates
# both with and without the leading article cover mass/plural nouns ("food",
# "people", "mountains") without reading as broken English.
TEMPLATES = [
    "a photo of a {}.",
    "a photo of {}.",
    "a close-up photo of a {}.",
    "a cropped photo of a {}.",
    "a bright photo of a {}.",
    "a good photo of a {}.",
    "a photo of one {}.",
    "a photo of a small {}.",
    "a photo of a large {}.",
    "a blurry photo of a {}.",
    "a low resolution photo of a {}.",
    "a photo containing a {}.",
    "{}.",
]

# Noun phrases per category. Keep the names in sync with SubjectTag in Rawr.Core
# (case-insensitive) and the grouping with SubjectTaxonomy. Both group roots
# (Animal, Vehicle, Nature) and their leaves (Dog, Cat, ...) get their own
# embedding; the runtime scores them with a parent-gated softmax, so a group's
# terms should describe the *generic* member while leaf terms stay specific.
#
# Bird deliberately avoids "in flight" / "flying" — those phrases pull airborne
# human poses (gymnasts, divers) and were a real false-positive source. The
# terms anchor on bird morphology instead.
TAG_TERMS = {
    # ── Standalone categories ──
    "Person": ["person", "people", "man", "woman", "human", "group of people", "portrait of a person"],
    "Food": ["food", "meal", "dish", "plate of food", "restaurant meal", "dessert"],
    "Architecture": ["building", "architecture", "cityscape", "city street", "house", "skyscraper", "building interior"],

    # ── Animal group + leaves ──
    "Animal": ["animal", "pet", "wild animal", "creature"],
    "Dog": ["dog", "puppy"],
    "Cat": ["cat", "kitten"],
    "Bird": ["bird", "small bird", "perched bird", "songbird", "duck", "owl"],
    "Horse": ["horse", "pony"],
    "Wildlife": ["wild animal", "deer", "fox", "bear", "elephant", "lion"],

    # ── Vehicle group + leaves ──
    "Vehicle": ["vehicle", "motor vehicle", "mode of transport"],
    "Car": ["car", "automobile", "sports car"],
    "Plane": ["airplane", "aircraft", "jet plane"],
    "Bike": ["bicycle", "motorcycle"],
    "Boat": ["boat", "ship", "sailboat"],
    "Train": ["train", "locomotive"],

    # ── Nature group (was Landscape) + leaves ──
    "Nature": ["landscape", "nature", "natural scenery", "the outdoors", "wilderness"],
    "Mountain": ["mountain", "mountain range", "mountain peak"],
    "Forest": ["forest", "woodland", "trees in a forest"],
    "Water": ["lake", "sea", "river", "ocean", "waterfall"],
    "Beach": ["beach", "sandy beach", "seashore"],
    "Sky": ["sky", "sunset", "clouds in the sky", "starry night sky"],
}

# None-of-the-above anchor. The runtime scores this in every softmax (top-level
# and per-group leaf) so its probability mass is subtracted from the real
# categories — this is the lever that stops an uncalibrated category from
# "winning by default" on a frame it doesn't actually contain. Written as full
# prompts (not templated): these are generic "just a photo" anchors, not a noun.
# The runtime keys on the name "background" (SubjectClassifier.BackgroundTagName)
# and never emits it as a tag.
BACKGROUND_PROMPTS = [
    "a photo",
    "a snapshot",
    "a random photo",
    "a photo of something",
    "an abstract image",
    "a texture",
    "a screenshot",
    "a document",
    "a blurry photo",
    "an indoor scene",
    "a close-up of an object",
    "a still life",
]


def prompts_for(terms):
    """Cross each noun phrase with every template."""
    return [tpl.format(term) for term in terms for tpl in TEMPLATES]


# name -> prompt list. Categories go through the template ensemble; the
# background anchor uses its hand-written prompts verbatim.
TAG_PROMPTS = {name: prompts_for(terms) for name, terms in TAG_TERMS.items()}
TAG_PROMPTS["background"] = BACKGROUND_PROMPTS


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
