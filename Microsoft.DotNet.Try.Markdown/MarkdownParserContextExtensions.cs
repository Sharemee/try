﻿using System;
using Markdig;
using Markdig.Helpers;

namespace Microsoft.DotNet.Try.Markdown
{
    public static class MarkdownParserContextExtensions
    {
        private const string DefaultsKey = nameof(AddDefaultCodeBlockAnnotations);

        public static MarkdownParserContext AddDefaultCodeBlockAnnotations(
            this MarkdownParserContext context,
            Action<DefaultCodeBlockAnnotations> configure)
        {
            DefaultCodeBlockAnnotations defaults;

            if (context.Properties.TryGetValue(DefaultsKey, out object value) &&
                value is DefaultCodeBlockAnnotations d)
            {
                defaults = d;
            }
            else
            {
                defaults = new DefaultCodeBlockAnnotations();
                context.Properties.Add(DefaultsKey, defaults);
            }

            configure(defaults);

            return context;
        }

        public static bool TryGetDefaultCodeBlockAnnotations(
            this MarkdownParserContext context,
            out DefaultCodeBlockAnnotations defaults)
        {
            object d = null;
            defaults = null;

            if (context?.Properties.TryGetValue(DefaultsKey, out d) == true)
            {
                return (defaults = d as DefaultCodeBlockAnnotations) != null;
            }
            else
            {
                return false;
            }
        }
    }
}