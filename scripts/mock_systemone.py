"""Offline stand-in for the Foundry System One endpoint, so the spike can be built and run
before the real Microsoft-Decision-1 deployment exists. It returns well-formed but fake answers:
it only checks our request shape and parsing, never model quality.

    python scripts/mock_systemone.py            # listens on http://localhost:8765
    MATCHLENS_DECISION_ENDPOINT=http://localhost:8765 MATCHLENS_DECISION_KEY=x \
    MATCHLENS_DECISION_DEPLOYMENT=mock dotnet run --project spikes/DecisionSpike
"""
import json
import random
from http.server import BaseHTTPRequestHandler, HTTPServer

PATH = "/providers/microsoft/v1/systemone"


def distribution(keys):
    weights = [random.random() ** 3 for _ in keys]
    total = sum(weights)
    return {k: round(w / total, 6) for k, w in zip(keys, weights)}


def answer(question):
    kind = question.get("type")
    if kind == "choice":
        probs = distribution(list(question["criteria"]))
        best = max(probs, key=probs.get)
        return {"type": "choice", "choice": best, "probabilities": probs, "confidence": round(probs[best], 3)}
    if kind == "score":
        probs = distribution([str(i) for i in range(len(question["criteria"]))])
        return {"type": "score", "score": round(sum(int(k) * p for k, p in probs.items()), 6),
                "probabilities": probs, "confidence": 0.5}
    if kind == "noul":
        return {"type": "noul", "noul": round(random.random(), 3)}
    return {"type": "refusal"}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != PATH or not self.headers.get("api-key"):
            self.send_error(404 if self.path != PATH else 401)
            return
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        missing = [f for f in ("model", "state", "questions") if f not in body]
        if missing:
            self.send_error(400, "missing " + ", ".join(missing))
            return
        reply = {
            "model": body["model"],
            "answers": {name: answer(q) for name, q in body["questions"].items()},
            "usage": {"input_tokens": len(body["state"]) // 4, "output_tokens": 0},
        }
        data = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    print(f"Mock System One on http://localhost:8765{PATH}")
    HTTPServer(("localhost", 8765), Handler).serve_forever()
