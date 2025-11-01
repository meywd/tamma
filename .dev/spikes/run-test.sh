#!/bin/bash
# Quick test runner script for AI provider POC

set -e

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}AI Provider POC Test Runner${NC}"
echo ""

# Check if tsx is installed
if ! command -v tsx &> /dev/null; then
    echo -e "${YELLOW}tsx not found. Installing...${NC}"
    npm install -g tsx
fi

# Check for .env file
if [ -f .env ]; then
    echo -e "${GREEN}Loading environment variables from .env${NC}"
    export $(cat .env | xargs)
else
    echo -e "${YELLOW}No .env file found. Using system environment variables.${NC}"
    echo "To use .env file: cp .env.example .env (and fill in your API keys)"
    echo ""
fi

# Default values
PROVIDER=${1:-gemini}
SCENARIO=${2:-issue-analysis}

echo -e "${GREEN}Running test:${NC}"
echo "  Provider: $PROVIDER"
echo "  Scenario: $SCENARIO"
echo ""

# Run the test
tsx test-providers-poc.ts --provider "$PROVIDER" --scenario "$SCENARIO"

echo ""
echo -e "${GREEN}✓ Test complete!${NC}"
echo ""
echo "Usage examples:"
echo "  ./run-test.sh gemini issue-analysis"
echo "  ./run-test.sh openai code-generation"
echo "  ./run-test.sh anthropic test-generation"
