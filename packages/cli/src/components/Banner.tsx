import React from 'react';
import { Box, Text } from 'ink';
import { colorProp } from '../colors.js';

interface BannerProps {
  version: string;
}

const LOGO = `  ╔╦╗╔═╗╔╦╗╔╦╗╔═╗
   ║ ╠═╣║║║║║║╠═╣
   ╩ ╩ ╩╩ ╩╩ ╩╩ ╩`;

export default function Banner({ version }: BannerProps): React.JSX.Element {
  return (
    <Box flexDirection="column" paddingX={1}>
      <Text {...colorProp('cyan')} bold>{LOGO}</Text>
      <Text {...colorProp('gray')}>  AI-powered autonomous development</Text>
      <Text {...colorProp('gray')}>  v{version}</Text>
    </Box>
  );
}

export function printBanner(version: string): void {
  const c = process.env['NO_COLOR'] ? (s: string) => s : (s: string) => `\x1b[36m${s}\x1b[0m`;
  const gray = process.env['NO_COLOR'] ? (s: string) => s : (s: string) => `\x1b[90m${s}\x1b[0m`;
  console.log(c(LOGO));
  console.log(gray('  AI-powered autonomous development'));
  console.log(gray(`  v${version}`));
  console.log();
}
