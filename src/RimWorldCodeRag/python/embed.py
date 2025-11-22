#!/usr/bin/env python3
"""Utility script to generate embeddings via HuggingFace transformers."""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Dict, List

import torch
from transformers import AutoModel, AutoTokenizer


def _load_model(model_dir: str):
    tokenizer = AutoTokenizer.from_pretrained(model_dir)
    model = AutoModel.from_pretrained(model_dir)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model.to(device)
    model.eval()
    return tokenizer, model, device


def _prepare_text(item: Dict[str, Any], mode: str = "passage") -> str:
    text = item.get("text") or ""
    if not text.strip():
        text = item.get("preview") or ""
    text = text.strip()
    # Prefix for e5 family embeddings (passage or query)
    if text:
        return f"{mode}: {text}"
    return f"{mode}: "


def _encode(items: List[Dict[str, Any]], tokenizer, model, max_length: int, device: torch.device, mode: str = "passage") -> List[List[float]]:
    inputs = [ _prepare_text(item, mode) for item in items ]
    encoded = tokenizer(
        inputs,
        padding=True,
        truncation=True,
        max_length=max_length,
        return_tensors="pt",
    )
    encoded = { key: value.to(device) for key, value in encoded.items() }
    with torch.no_grad():
        outputs = model(**encoded)
        # Mean pool last hidden state
        embeddings = outputs.last_hidden_state.mean(dim=1)
    embeddings = torch.nn.functional.normalize(embeddings, p=2, dim=1)
    return embeddings.cpu().tolist()


def _handle_encode(args: argparse.Namespace) -> None:
    with open(args.input, "r", encoding="utf-8") as fp:
        payload = json.load(fp)

    items = payload.get("items", [])
    if not isinstance(items, list):
        raise ValueError("Input JSON must contain an 'items' array")

    mode = payload.get("mode", "passage")
    if mode not in ("passage", "query"):
        raise ValueError(f"Invalid mode '{mode}'; must be 'passage' or 'query'")

    tokenizer, model, device = _load_model(args.model)
    vectors = _encode(items, tokenizer, model, args.max_length, device, mode)

    with open(args.output, "w", encoding="utf-8") as fp:
        json.dump({"vectors": vectors}, fp)


def main() -> None:
    parser = argparse.ArgumentParser(description="Embedding helper")
    subparsers = parser.add_subparsers(dest="command", required=True)

    encode_parser = subparsers.add_parser("encode", help="Generate embeddings")
    encode_parser.add_argument("--model", required=True, help="Model directory")
    encode_parser.add_argument("--input", required=True, help="Input JSON file")
    encode_parser.add_argument("--output", required=True, help="Output JSON file")
    encode_parser.add_argument("--max-length", type=int, default=256, help="Token truncation length")

    args = parser.parse_args()

    try:
        if args.command == "encode":
            _handle_encode(args)
        else:
            raise ValueError(f"Unknown command: {args.command}")
    except Exception as exc:  # noqa: BLE001
        print(f"error: {exc}", file=sys.stderr)
        raise


if __name__ == "__main__":
    torch.set_grad_enabled(False)
    main()
