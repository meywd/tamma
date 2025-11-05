/**
 * Optimizes the OG image for better social media sharing performance
 * Target: Reduce file size to <300KB while maintaining visual quality
 * Recommended OG image size: 1200x1200 for square, 1200x630 for landscape
 */

import sharp from 'sharp';
import { join } from 'path';
import { existsSync, copyFileSync, statSync } from 'fs';

const inputPath = join(__dirname, '..', 'public', 'assets', 'og-image.png');
const backupPath = join(__dirname, '..', 'public', 'assets', 'og-image-original.png');
const outputPath = join(__dirname, '..', 'public', 'assets', 'og-image.png');

async function optimizeImage() {
  try {
    // Check if input exists
    if (!existsSync(inputPath)) {
      console.error('❌ Error: og-image.png not found');
      process.exit(1);
    }

    // Get original file size
    const originalSize = statSync(inputPath).size;
    console.log(`📊 Original size: ${(originalSize / 1024).toFixed(2)} KB`);

    // Backup original if not already backed up
    if (!existsSync(backupPath)) {
      copyFileSync(inputPath, backupPath);
      console.log('💾 Backed up original to og-image-original.png');
    }

    // Optimize image: resize to 1200x1200 and compress
    await sharp(inputPath)
      .resize(1200, 1200, {
        fit: 'contain',
        background: { r: 255, g: 255, b: 255, alpha: 1 }
      })
      .png({
        quality: 85,
        compressionLevel: 9,
        effort: 10
      })
      .toFile(outputPath + '.tmp');

    // Check optimized size
    const optimizedSize = statSync(outputPath + '.tmp').size;
    console.log(`📊 Optimized size: ${(optimizedSize / 1024).toFixed(2)} KB`);

    if (optimizedSize < 300 * 1024) {
      // Replace original with optimized version
      copyFileSync(outputPath + '.tmp', outputPath);
      console.log(`✓ Successfully optimized og-image.png`);
      console.log(`  Size reduction: ${((1 - optimizedSize / originalSize) * 100).toFixed(1)}%`);
    } else {
      console.log('⚠️  Optimized file is still >300KB, trying more aggressive compression...');

      // Try more aggressive optimization
      await sharp(inputPath)
        .resize(1200, 1200, {
          fit: 'contain',
          background: { r: 255, g: 255, b: 255, alpha: 1 }
        })
        .png({
          quality: 75,
          compressionLevel: 9,
          effort: 10
        })
        .toFile(outputPath + '.tmp2');

      const aggressiveSize = statSync(outputPath + '.tmp2').size;
      console.log(`📊 Aggressive size: ${(aggressiveSize / 1024).toFixed(2)} KB`);

      if (aggressiveSize < 300 * 1024) {
        copyFileSync(outputPath + '.tmp2', outputPath);
        console.log(`✓ Successfully optimized og-image.png with aggressive compression`);
        console.log(`  Size reduction: ${((1 - aggressiveSize / originalSize) * 100).toFixed(1)}%`);
      } else {
        console.error('❌ Unable to optimize below 300KB. Manual optimization may be required.');
        process.exit(1);
      }
    }
  } catch (error) {
    console.error('❌ Error optimizing image:', error);
    process.exit(1);
  }
}

optimizeImage();
