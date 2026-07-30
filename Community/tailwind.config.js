/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        "./**/*.razor",
        "./**/*.cshtml",
        "./wwwroot/**/*.html",
        "./wwwroot/**/*.js",
        "../TodoSuite.Community.Shared/**/*.razor"
    ],
    theme: { extend: {} },
    plugins: []
};
