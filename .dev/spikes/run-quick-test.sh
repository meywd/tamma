#!/bin/bash
# Quick test runner for AI provider benchmark

set -e

echo "🧪 Running Quick Benchmark Test"
echo "================================"
echo ""

# Load environment variables
if [ -f .env ]; then
    echo "📁 Loading API keys from .env..."
    export $(cat .env | grep -v '^#' | xargs)
else
    echo "⚠️  No .env file found. Using system environment variables."
fi

echo ""
echo "🔍 Detecting available providers..."
echo ""

AVAILABLE_PROVIDERS=""

# Check Google Gemini
if [ -n "$GOOGLE_AI_API_KEY" ] && [ "$GOOGLE_AI_API_KEY" != "your-google-api-key-here" ]; then
    echo "  ✅ Google Gemini (GOOGLE_AI_API_KEY set)"
    AVAILABLE_PROVIDERS="${AVAILABLE_PROVIDERS}gemini,"
fi

# Check OpenAI
if [ -n "$OPENAI_API_KEY" ] && [ "$OPENAI_API_KEY" != "your-openai-api-key-here" ]; then
    echo "  ✅ OpenAI (OPENAI_API_KEY set)"
    AVAILABLE_PROVIDERS="${AVAILABLE_PROVIDERS}openai,"
fi

# Check Anthropic
if [ -n "$ANTHROPIC_API_KEY" ] && [ "$ANTHROPIC_API_KEY" != "your-anthropic-api-key-here" ]; then
    echo "  ✅ Anthropic Claude (ANTHROPIC_API_KEY set)"
    AVAILABLE_PROVIDERS="${AVAILABLE_PROVIDERS}anthropic,"
fi

# Check OpenRouter
if [ -n "$OPENROUTER_API_KEY" ] && [ "$OPENROUTER_API_KEY" != "your-openrouter-api-key-here" ]; then
    echo "  ✅ OpenRouter (OPENROUTER_API_KEY set)"
    AVAILABLE_PROVIDERS="${AVAILABLE_PROVIDERS}openrouter,"
fi

# Check Ollama
if curl -s http://localhost:11434/api/tags > /dev/null 2>&1; then
    echo "  ✅ Ollama (running locally)"
    AVAILABLE_PROVIDERS="${AVAILABLE_PROVIDERS}ollama,"
fi

# Remove trailing comma
AVAILABLE_PROVIDERS=${AVAILABLE_PROVIDERS%,}

if [ -z "$AVAILABLE_PROVIDERS" ]; then
    echo ""
    echo "❌ No providers available!"
    echo ""
    echo "Please set up at least one API key:"
    echo "  1. Copy .env.example to .env"
    echo "  2. Add your API key(s)"
    echo "  3. Run this script again"
    echo ""
    echo "Or install Ollama for local testing:"
    echo "  curl -fsSL https://ollama.ai/install.sh | sh"
    echo "  ollama pull codellama:7b"
    echo "  ollama serve"
    echo ""
    exit 1
fi

echo ""
echo "🎯 Running benchmark with: $AVAILABLE_PROVIDERS"
echo "   Iterations: 1 (quick mode)"
echo "   Scenarios: All 4 scenarios"
echo ""
echo "⏱️  Estimated time: 2-5 minutes"
echo ""

# Run the benchmark
tsx run-benchmark.ts --providers "$AVAILABLE_PROVIDERS" --iterations 1

echo ""
echo "✅ Quick test complete!"
echo ""
echo "📊 Results saved to:"
echo "   - results/batch-results-*.json (full data)"
echo "   - results/benchmark-report-*.md (comparison report)"
echo ""
echo "📖 To view the report:"
echo "   cat results/benchmark-report-*.md | less"
echo ""
echo "🚀 To run full benchmark (3 iterations):"
echo "   tsx run-benchmark.ts --providers $AVAILABLE_PROVIDERS --iterations 3"
echo ""
