using System;
using System.Configuration;
using CrossStitch.Shared;
using CrossStitch.Shared.Pinterest;
using UploadPatterns;

namespace Uploader.Helpers;

/// <summary>
/// Boots the shared <c>CrossStitch.Shared</c> Pinterest / link helpers using
/// the Uploader's existing App.config / App.private.config keys. The shared
/// library has no <c>ConfigurationManager</c> dependency; this is the one
/// adapter layer that bridges the two.
/// </summary>
public static class HelperFactory
{
    public static PinterestOAuthClient CreatePinterestOAuthClient()
    {
        return new PinterestOAuthClient(new PinterestOAuthConfig
        {
            ClientId = AppSetting("PinterestClientId", required: true)!,
            ClientSecret = AppSetting("PinterestClientSecret", required: true)!,
            RedirectUri = AppSetting("PinterestRedirectUri") ?? string.Empty,
            TokenStorePath = PlatformConfig.ResolvePinterestTokenPath(),
        });
    }

    public static PatternLinkHelper CreatePatternLinkHelper()
    {
        var siteBaseUrl =
            AppSetting("SiteBaseUrl") ??
            AppSetting("PinterestLinkUrl") ??
            throw new ConfigurationErrorsException(
                "SiteBaseUrl (or fallback PinterestLinkUrl) must be configured in appSettings.");

        return new PatternLinkHelper(new PatternLinkConfig
        {
            SiteBaseUrl = siteBaseUrl,
            ImageBaseUrl = ResolveImageBaseUrl(),
            PhotoPrefix = AppSetting("S3PhotoPrefix") ?? "photos",
            AlbumUrlTemplate = AppSetting("AlbumUrlTemplate") ?? string.Empty,
        });
    }

    public static PinterestUploader CreatePinterestUploader()
    {
        var oauth = CreatePinterestOAuthClient();
        var link = CreatePatternLinkHelper();
        var config = new PinterestUploaderConfig
        {
            BoardsCsvPath = AppSetting("PinterestBoardsCsvPath"),
            DefaultBoardId = AppSetting("PinterestBoardId"),
        };
        return new PinterestUploader(config, link, oauth);
    }

    /// <summary>
    /// Adapter: convert Uploader's WPF-bound <see cref="PatternInfo"/> (with
    /// PDF parsing logic) into the portable <see cref="PinPatternInfo"/> DTO
    /// the shared library expects.
    /// </summary>
    public static PinPatternInfo ToPinPatternInfo(this PatternInfo info)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));
        return new PinPatternInfo
        {
            AlbumId = info.AlbumId,
            DesignId = info.DesignID,
            NPage = info.NPage ?? string.Empty,
            Title = info.Title ?? string.Empty,
            Description = info.Description ?? string.Empty,
            Notes = info.Notes ?? string.Empty,
            Width = info.Width,
            Height = info.Height,
            NColors = info.NColors,
        };
    }

    private static string ResolveImageBaseUrl()
    {
        var explicitBase = AppSetting("S3PublicBaseUrl");
        if (!string.IsNullOrWhiteSpace(explicitBase)) return explicitBase;
        var bucket = AppSetting("S3BucketName") ?? "cross-stitch-designs";
        return $"https://{bucket}.s3.amazonaws.com";
    }

    private static string? AppSetting(string key, bool required = false)
    {
        var v = ConfigurationManager.AppSettings[key];
        if (string.IsNullOrWhiteSpace(v))
        {
            if (required)
                throw new ConfigurationErrorsException($"appSettings/{key} is missing or empty.");
            return null;
        }
        return v;
    }
}
