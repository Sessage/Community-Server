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
    theme: { extend: {} },
    plugins: []
};
