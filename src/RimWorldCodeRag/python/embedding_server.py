#!/usr/bin/env python3
"""Persistent embedding server to eliminate cold-start overhead."""

from __future__ import annotations

import argparse
import json
import logging
import sys
from typing import Any, Dict, List

import torch
from flask import Flask, request, jsonify
from transformers import AutoModel, AutoTokenizer

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='[embedding-server] %(message)s'
)
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Global state (loaded once at startup)
model = None
tokenizer = None
device = None
max_length = 512


def _prepare_text(item: Dict[str, Any], mode: str = "passage") -> str:
    """Prepare text for embedding with appropriate prefix."""
    text = item.get("text") or ""
    if not text.strip():
        text = item.get("preview") or ""
    text = text.strip()
    
    # Prefix for e5 family embeddings
    if text:
        return f"{mode}: {text}"
    return f"{mode}: "


def _encode_batch(items: List[Dict[str, Any]], mode: str = "passage") -> List[List[float]]:
    """Encode a batch of items into embeddings."""
    if not items:
        return []
    
    texts = [_prepare_text(item, mode) for item in items]
    
    encoded = tokenizer(
        texts,
        padding=True,
        truncation=True,
        max_length=max_length,
        return_tensors="pt",
    )
    encoded = {key: value.to(device) for key, value in encoded.items()}
    
    with torch.no_grad():
        outputs = model(**encoded)
        # Mean pool last hidden state
        embeddings = outputs.last_hidden_state.mean(dim=1)
    
    embeddings = torch.nn.functional.normalize(embeddings, p=2, dim=1)
    return embeddings.cpu().tolist()


@app.route('/health', methods=['GET'])
def health():
    """Health check endpoint."""
    return jsonify({
        "status": "healthy",
        "device": str(device),
        "model_loaded": model is not None
    })


@app.route('/embed', methods=['POST'])
def embed():
    """Embed endpoint that accepts JSON payload and returns embeddings."""
    try:
        payload = request.get_json()
        if not payload:
            return jsonify({"error": "No JSON payload"}), 400
        
        items = payload.get("items", [])
        if not isinstance(items, list):
            return jsonify({"error": "items must be a list"}), 400
        
        mode = payload.get("mode", "passage")
        if mode not in ("passage", "query"):
            return jsonify({"error": f"Invalid mode '{mode}'"}), 400
        
        if not items:
            return jsonify({"vectors": []})
        
        # Process in batches to avoid OOM
        batch_size = 128
        all_vectors = []
        
        for i in range(0, len(items), batch_size):
            batch = items[i:i + batch_size]
            vectors = _encode_batch(batch, mode)
            all_vectors.extend(vectors)
        
        return jsonify({"vectors": all_vectors})
    
    except Exception as e:
        logger.error(f"Error processing request: {e}", exc_info=True)
        return jsonify({"error": str(e)}), 500


def main() -> None:
    global model, tokenizer, device, max_length
    
    parser = argparse.ArgumentParser(description="Embedding server")
    parser.add_argument("--model", required=True, help="Model directory")
    parser.add_argument("--max-length", type=int, default=512, help="Max sequence length")
    parser.add_argument("--host", default="127.0.0.1", help="Server host")
    parser.add_argument("--port", type=int, default=5000, help="Server port")
    args = parser.parse_args()
    
    max_length = args.max_length
    
    logger.info(f"Loading model from {args.model}")
    tokenizer = AutoTokenizer.from_pretrained(args.model)
    model = AutoModel.from_pretrained(args.model)
    
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model.to(device)
    model.eval()
    
    logger.info(f"Model loaded on device: {device}")
    logger.info(f"Starting server on {args.host}:{args.port}")
    
    app.run(host=args.host, port=args.port, threaded=True)


if __name__ == "__main__":
    main()
