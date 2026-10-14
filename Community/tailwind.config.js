/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        "./**/*.razor",
        "./**/*.cshtml",
        "./wwwroot/**/*.html",
        "./wwwroot/**/*.js",
        "../TodoSuite.Community.Shared/**/*.razor",
        "../TodoSuite.Mobile/**/*.razor"
    ],
    // Preserve the existing palette and utility scales during the v4 security upgrade.
    theme: require('./Styles/tailwind.compat-theme.json'),
    plugins: []
};
