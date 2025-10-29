#!/usr/bin/env node
/**
 * @tamma/cli
 * Ink-based CLI interface for the Tamma platform
 */

import { Command } from 'commander';

const program = new Command();

program
  .name('tamma')
  .description('AI-powered autonomous development orchestration platform')
  .version('0.1.0');

program
  .command('start')
  .description('Start Tamma in orchestrator or worker mode')
  .option('-m, --mode <mode>', 'Mode: orchestrator, worker, or standalone', 'standalone')
  .action((options) => {
    console.log(`Starting Tamma in ${options.mode} mode...`);
    console.log('CLI implementation coming soon (Epic 1, Story 1-9)');
  });

program.parse();
