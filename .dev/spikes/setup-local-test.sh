#!/bin/bash
# Quick setup script for local benchmark testing

set -e

echo "🚀 AI Provider Benchmark - Local Setup"
echo "======================================"
echo ""

# Check for tsx
if ! command -v tsx &> /dev/null; then
    echo "📦 Installing tsx..."
    npm install -g tsx
else
    echo "✅ tsx already installed"
fi

# Check for TypeScript
if ! command -v tsc &> /dev/null; then
    echo "📦 Installing TypeScript..."
    npm install -g typescript
else
    echo "✅ TypeScript already installed"
fi

echo ""
echo "🔑 API Key Setup"
echo "----------------"

# Check for .env file
if [ ! -f .env ]; then
    echo "📝 Creating .env file from template..."
    cp .env.example .env
    echo "✅ .env file created"
    echo ""
    echo "⚠️  Please edit .env and add your API keys:"
    echo "    nano .env"
    echo ""
else
    echo "✅ .env file already exists"
fi

# Check if API keys are set
if [ -f .env ]; then
    source .env

    echo "Checking API keys..."
    echo ""

    if [ -n "$GOOGLE_AI_API_KEY" ] && [ "$GOOGLE_AI_API_KEY" != "your-google-api-key-here" ]; then
        echo "  ✅ GOOGLE_AI_API_KEY is set"
        HAS_KEY=true
    else
        echo "  ⚠️  GOOGLE_AI_API_KEY not set (recommended - free tier)"
        echo "     Get key: https://aistudio.google.com/app/apikey"
    fi

    if [ -n "$OPENAI_API_KEY" ] && [ "$OPENAI_API_KEY" != "your-openai-api-key-here" ]; then
        echo "  ✅ OPENAI_API_KEY is set"
        HAS_KEY=true
    else
        echo "  ⚠️  OPENAI_API_KEY not set (optional - $5 trial)"
    fi

    if [ -n "$ANTHROPIC_API_KEY" ] && [ "$ANTHROPIC_API_KEY" != "your-anthropic-api-key-here" ]; then
        echo "  ✅ ANTHROPIC_API_KEY is set"
        HAS_KEY=true
    else
        echo "  ⚠️  ANTHROPIC_API_KEY not set (optional)"
    fi

    if [ -n "$OPENROUTER_API_KEY" ] && [ "$OPENROUTER_API_KEY" != "your-openrouter-api-key-here" ]; then
        echo "  ✅ OPENROUTER_API_KEY is set"
        HAS_KEY=true
    else
        echo "  ⚠️  OPENROUTER_API_KEY not set (optional - free models)"
    fi
fi

echo ""
echo "📊 Ready to Test!"
echo "-----------------"

if [ "$HAS_KEY" = true ]; then
    echo "You can now run the benchmark:"
    echo ""
    echo "  # Quick test (1 iteration, 2-3 minutes)"
    echo "  ./run-quick-test.sh"
    echo ""
    echo "  # Or manually"
    echo "  source .env"
    echo "  tsx run-benchmark.ts --quick"
    echo ""
else
    echo "⚠️  No API keys configured yet."
    echo ""
    echo "Next steps:"
    echo "  1. Edit .env file:"
    echo "     nano .env"
    echo ""
    echo "  2. Add at least one API key (Google Gemini recommended)"
    echo ""
    echo "  3. Run quick test:"
    echo "     ./run-quick-test.sh"
    echo ""
fi

echo "📖 For more information, see TESTING.md"
