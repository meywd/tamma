/**
 * TypeScript/JavaScript Chunker
 *
 * Uses the TypeScript compiler API to parse and chunk TypeScript and JavaScript files
 * into semantic units (functions, classes, interfaces, etc.).
 */

import * as ts from 'typescript';
import { BaseChunker } from './base-chunker.js';
import type {
  CodeChunk,
  ChunkingStrategy,
  SupportedLanguage,
  ChunkType,
} from '../types.js';

/**
 * Extract information from a TypeScript AST node
 */
interface NodeInfo {
  name: string;
  chunkType: ChunkType;
  startLine: number;
  endLine: number;
  content: string;
  docstring?: string | undefined;
  exports: string[];
  isExported: boolean;
}

/**
 * TypeScript and JavaScript code chunker
 */
export class TypeScriptChunker extends BaseChunker {
  readonly supportedLanguages: SupportedLanguage[] = ['typescript', 'javascript'];

  /**
   * Chunk TypeScript/JavaScript code into semantic units
   */
  async chunk(
    content: string,
    filePath: string,
    fileId: string,
    strategy: ChunkingStrategy,
  ): Promise<CodeChunk[]> {
    const chunks: CodeChunk[] = [];
    let chunkIndex = 0;

    // Parse the content using TypeScript compiler API
    const sourceFile = ts.createSourceFile(
      filePath,
      content,
      ts.ScriptTarget.Latest,
      true, // setParentNodes
      this.getScriptKind(filePath),
    );

    // Extract imports
    const imports = this.extractImports(sourceFile);

    // Create imports chunk if preserveImports is enabled
    if (strategy.preserveImports && imports.content) {
      const importChunk = this.createChunk(
        imports.content,
        filePath,
        fileId,
        chunkIndex++,
        {
          chunkType: 'imports',
          name: 'imports',
          startLine: imports.startLine,
          endLine: imports.endLine,
          language: strategy.language,
          imports: imports.modules,
          exports: [],
        },
      );
      chunks.push(importChunk);
    }

    // Extract top-level declarations
    const declarations = this.extractDeclarations(sourceFile, content);

    // Group small declarations if enabled
    const groupedDeclarations = strategy.groupRelatedCode
      ? this.groupSmallDeclarations(declarations, strategy.maxChunkTokens)
      : declarations.map(d => [d]);

    // Create chunks for each declaration or group
    for (const group of groupedDeclarations) {
      const firstDecl = group[0]!;
      const lastDecl = group[group.length - 1]!;

      // Combine content for grouped declarations
      const combinedContent = group.map(d => d.content).join('\n\n');
      const combinedExports = group.flatMap(d => d.exports);
      // When all declarations share the same chunkType, preserve it
      // instead of collapsing to 'block'.
      const allSameType = group.every(d => d.chunkType === firstDecl.chunkType);
      const name = group.length === 1
        ? firstDecl.name
        : `${firstDecl.name}...${lastDecl.name}`;
      const chunkType = group.length === 1
        ? firstDecl.chunkType
        : allSameType
          ? firstDecl.chunkType
          : 'block';

      // Check if content exceeds token limit
      if (this.estimateTokens(combinedContent) > strategy.maxChunkTokens) {
        // Split into smaller chunks
        const splitChunks = this.splitByTokenLimit(
          combinedContent,
          firstDecl.startLine,
          strategy.maxChunkTokens,
          strategy.overlapTokens,
        );

        for (const split of splitChunks) {
          const chunk = this.createChunk(
            split.content,
            filePath,
            fileId,
            chunkIndex++,
            {
              chunkType,
              name: `${name} (part ${chunkIndex})`,
              startLine: split.startLine,
              endLine: split.endLine,
              language: strategy.language,
              imports: imports.modules,
              exports: combinedExports,
              docstring: firstDecl.docstring,
            },
          );
          chunks.push(chunk);
        }
      } else {
        const chunk = this.createChunk(
          combinedContent,
          filePath,
          fileId,
          chunkIndex++,
          {
            chunkType,
            name,
            startLine: firstDecl.startLine,
            endLine: lastDecl.endLine,
            language: strategy.language,
            imports: imports.modules,
            exports: combinedExports,
            docstring: firstDecl.docstring,
          },
        );
        chunks.push(chunk);
      }
    }

    // If no declarations found, create a single module chunk
    if (chunks.length === 0 || (chunks.length === 1 && chunks[0]!.chunkType === 'imports')) {
      const moduleContent = strategy.preserveImports
        ? content.slice(imports.endPosition).trim()
        : content;

      if (moduleContent) {
        const moduleStartLine = strategy.preserveImports ? imports.endLine + 1 : 1;
        const chunk = this.createChunk(
          moduleContent,
          filePath,
          fileId,
          chunkIndex++,
          {
            chunkType: 'module',
            name: 'module',
            startLine: moduleStartLine,
            endLine: this.countLines(content),
            language: strategy.language,
            imports: imports.modules,
            exports: [],
          },
        );
        chunks.push(chunk);
      }
    }

    return chunks;
  }

  /**
   * Get TypeScript script kind from file extension
   */
  private getScriptKind(filePath: string): ts.ScriptKind {
    const ext = filePath.toLowerCase().split('.').pop();
    switch (ext) {
      case 'ts':
        return ts.ScriptKind.TS;
      case 'tsx':
        return ts.ScriptKind.TSX;
      case 'js':
      case 'mjs':
      case 'cjs':
        return ts.ScriptKind.JS;
      case 'jsx':
        return ts.ScriptKind.JSX;
      default:
        return ts.ScriptKind.TS;
    }
  }

  /**
   * Extract import statements from source file
   */
  private extractImports(sourceFile: ts.SourceFile): {
    content: string;
    modules: string[];
    startLine: number;
    endLine: number;
    endPosition: number;
  } {
    const imports: ts.Node[] = [];
    const modules: string[] = [];
    let endPosition = 0;

    ts.forEachChild(sourceFile, (node) => {
      if (ts.isImportDeclaration(node)) {
        imports.push(node);
        endPosition = Math.max(endPosition, node.end);

        // Extract module specifier
        if (node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
          modules.push(node.moduleSpecifier.text);
        }
      } else if (
        ts.isImportEqualsDeclaration(node) ||
        (ts.isExpressionStatement(node) &&
          ts.isCallExpression(node.expression) &&
          ts.isIdentifier(node.expression.expression) &&
          node.expression.expression.text === 'require')
      ) {
        imports.push(node);
        endPosition = Math.max(endPosition, node.end);
      }
    });

    if (imports.length === 0) {
      return { content: '', modules: [], startLine: 0, endLine: 0, endPosition: 0 };
    }

    const firstImport = imports[0]!;
    const lastImport = imports[imports.length - 1]!;
    const startLine = sourceFile.getLineAndCharacterOfPosition(firstImport.pos).line + 1;
    const endLine = sourceFile.getLineAndCharacterOfPosition(lastImport.end).line + 1;

    const content = sourceFile.text.slice(firstImport.pos, lastImport.end).trim();

    return { content, modules, startLine, endLine, endPosition };
  }

  /**
   * Extract top-level declarations from source file
   */
  private extractDeclarations(
    sourceFile: ts.SourceFile,
    _content: string,
  ): NodeInfo[] {
    const declarations: NodeInfo[] = [];

    const visit = (node: ts.Node, parentName?: string) => {
      const nodeInfo = this.getNodeInfo(node, sourceFile, parentName);
      if (nodeInfo) {
        declarations.push(nodeInfo);
      }

      // For classes, also extract methods
      if (ts.isClassDeclaration(node) && node.name) {
        const className = node.name.text;
        node.members.forEach((member) => {
          if (ts.isMethodDeclaration(member) || ts.isPropertyDeclaration(member)) {
            const memberInfo = this.getNodeInfo(member, sourceFile, className);
            if (memberInfo && this.estimateTokens(memberInfo.content) > 100) {
              // Only extract large methods/properties separately
              declarations.push(memberInfo);
            }
          }
        });
      }
    };

    ts.forEachChild(sourceFile, (node) => visit(node));

    return declarations;
  }

  /**
   * Get information about a node
   */
  private getNodeInfo(
    node: ts.Node,
    sourceFile: ts.SourceFile,
    _parentScope?: string,
  ): NodeInfo | null {
    let name = 'anonymous';
    let chunkType: ChunkType = 'block';
    let isExported = false;
    const exports: string[] = [];

    // Check for export modifiers
    if (ts.canHaveModifiers(node)) {
      const modifiers = ts.getModifiers(node);
      if (modifiers) {
        isExported = modifiers.some(
          (m) =>
            m.kind === ts.SyntaxKind.ExportKeyword ||
            m.kind === ts.SyntaxKind.DefaultKeyword,
        );
      }
    }

    // Function declarations
    if (ts.isFunctionDeclaration(node)) {
      name = node.name?.text ?? 'anonymous';
      chunkType = 'function';
      if (isExported) exports.push(name);
    }
    // Arrow functions / function expressions assigned to variables
    else if (ts.isVariableStatement(node)) {
      const declarations = node.declarationList.declarations;
      if (declarations.length > 0) {
        const decl = declarations[0]!;
        if (ts.isIdentifier(decl.name)) {
          name = decl.name.text;
          if (decl.initializer) {
            if (
              ts.isArrowFunction(decl.initializer) ||
              ts.isFunctionExpression(decl.initializer)
            ) {
              chunkType = 'function';
            }
          }
          if (isExported) exports.push(name);
        }
      }
    }
    // Class declarations
    else if (ts.isClassDeclaration(node)) {
      name = node.name?.text ?? 'anonymous';
      chunkType = 'class';
      if (isExported) exports.push(name);
    }
    // Interface declarations
    else if (ts.isInterfaceDeclaration(node)) {
      name = node.name.text;
      chunkType = 'interface';
      if (isExported) exports.push(name);
    }
    // Type alias declarations
    else if (ts.isTypeAliasDeclaration(node)) {
      name = node.name.text;
      chunkType = 'type';
      if (isExported) exports.push(name);
    }
    // Enum declarations
    else if (ts.isEnumDeclaration(node)) {
      name = node.name.text;
      chunkType = 'enum';
      if (isExported) exports.push(name);
    }
    // Method declarations (within classes)
    else if (ts.isMethodDeclaration(node)) {
      if (ts.isIdentifier(node.name)) {
        name = node.name.text;
      }
      chunkType = 'function';
    }
    // Export declarations
    else if (ts.isExportDeclaration(node)) {
      // Skip export declarations, they're typically re-exports
      return null;
    }
    // Import declarations - skip
    else if (ts.isImportDeclaration(node)) {
      return null;
    }
    // Other statements - skip small ones
    else {
      return null;
    }

    const startPos = node.getFullStart();
    const endPos = node.getEnd();
    const startLine = sourceFile.getLineAndCharacterOfPosition(startPos).line + 1;
    const endLine = sourceFile.getLineAndCharacterOfPosition(endPos).line + 1;
    const content = sourceFile.text.slice(startPos, endPos).trim();

    // Extract JSDoc comment if present
    const jsDocComment = this.extractJSDoc(node, sourceFile);

    return {
      name,
      chunkType,
      startLine,
      endLine,
      content,
      docstring: jsDocComment,
      exports,
      isExported,
    };
  }

  /**
   * Extract JSDoc comment from a node
   */
  private extractJSDoc(node: ts.Node, sourceFile: ts.SourceFile): string | undefined {
    const fullText = sourceFile.text;
    const nodeStart = node.getFullStart();
    const leadingComments = ts.getLeadingCommentRanges(fullText, nodeStart);

    if (!leadingComments || leadingComments.length === 0) {
      return undefined;
    }

    // Find the last JSDoc comment before the node
    for (let i = leadingComments.length - 1; i >= 0; i--) {
      const comment = leadingComments[i]!;
      const commentText = fullText.slice(comment.pos, comment.end);

      if (commentText.startsWith('/**')) {
        // Clean up the JSDoc comment
        return commentText
          .replace(/^\/\*\*/, '')
          .replace(/\*\/$/, '')
          .split('\n')
          .map((line) => line.replace(/^\s*\*\s?/, '').trim())
          .filter(Boolean)
          .join('\n');
      }
    }

    return undefined;
  }

  /**
   * Group small declarations together to reduce chunk count.
   * Only groups type-level declarations (types, interfaces, enums) of the same
   * kind together. Functions and classes are always kept as individual chunks
   * since they represent distinct semantic units.
   */
  private groupSmallDeclarations(
    declarations: NodeInfo[],
    maxTokens: number,
  ): NodeInfo[][] {
    const groups: NodeInfo[][] = [];
    let currentGroup: NodeInfo[] = [];
    let currentTokens = 0;
    let currentGroupType: ChunkType | null = null;

    // Only type-level declarations are eligible for grouping
    const groupableTypes = new Set<ChunkType>(['type', 'interface', 'enum', 'block']);
    const smallThreshold = maxTokens * 0.3; // Consider anything < 30% of max as "small"

    for (const decl of declarations) {
      const declTokens = this.estimateTokens(decl.content);

      // Functions and classes are always individual chunks
      if (!groupableTypes.has(decl.chunkType)) {
        if (currentGroup.length > 0) {
          groups.push(currentGroup);
          currentGroup = [];
          currentTokens = 0;
          currentGroupType = null;
        }
        groups.push([decl]);
        continue;
      }

      // If declaration is large, put it in its own group
      if (declTokens > smallThreshold) {
        if (currentGroup.length > 0) {
          groups.push(currentGroup);
          currentGroup = [];
          currentTokens = 0;
          currentGroupType = null;
        }
        groups.push([decl]);
        continue;
      }

      // Check if adding to current group would exceed limit or if types differ
      const typeMismatch = currentGroupType !== null && currentGroupType !== decl.chunkType;
      if (currentTokens + declTokens > maxTokens || typeMismatch) {
        if (currentGroup.length > 0) {
          groups.push(currentGroup);
        }
        currentGroup = [decl];
        currentTokens = declTokens;
        currentGroupType = decl.chunkType;
      } else {
        currentGroup.push(decl);
        currentTokens += declTokens;
        currentGroupType = decl.chunkType;
      }
    }

    // Don't forget the last group
    if (currentGroup.length > 0) {
      groups.push(currentGroup);
    }

    return groups;
  }
}
