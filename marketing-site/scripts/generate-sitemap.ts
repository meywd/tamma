/**
 * Generates sitemap.xml with current date
 * Run during build process to ensure lastmod dates are always current
 */

import { writeFileSync } from 'fs';
import { join } from 'path';

const currentDate = new Date().toISOString().split('T')[0]; // YYYY-MM-DD format

const sitemap = `<?xml version="1.0" encoding="UTF-8"?>
<!-- Task 6 (#85) - SEO Optimization: Sitemap for search engines -->
<!-- Auto-generated during build with current date -->
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        xsi:schemaLocation="http://www.sitemaps.org/schemas/sitemap/0.9
        http://www.sitemaps.org/schemas/sitemap/0.9/sitemap.xsd">

    <!-- Homepage -->
    <url>
        <loc>https://tamma.dev/</loc>
        <lastmod>${currentDate}</lastmod>
        <changefreq>weekly</changefreq>
        <priority>1.0</priority>
    </url>

</urlset>
`;

const sitemapPath = join(__dirname, '..', 'public', 'sitemap.xml');
writeFileSync(sitemapPath, sitemap, 'utf-8');

console.log(`✓ Generated sitemap.xml with date: ${currentDate}`);
