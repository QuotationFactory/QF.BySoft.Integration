﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using QF.BySoft.Entities.Repositories;
using QF.BySoft.LocalData.Helpers;
using QF.BySoft.LocalData.Models;

namespace QF.BySoft.LocalData;

public class MaterialMappingRepository : IMaterialMappingRepository
{
    private readonly string _applicationBasePath = ApplicationInfo.GetApplicationBasePath();

    public string GetMaterialIdFromKeywords(IEnumerable<string> keywords)
    {
        if (keywords == null)
        {
            throw new ArgumentNullException(nameof(keywords));
        }

        var keywordsList = keywords.ToList();
        if (!keywordsList.Any())
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(keywords));
        }

        var mappingList = GetMaterialMapListForGroupKeywordMapping();

        var materialId = GetMaterialIdFromKeywords(keywordsList, mappingList);

        return materialId ?? string.Empty;
    }

    public string GetMaterialIdFromArticle(string materialId)
    {
        if (string.IsNullOrWhiteSpace(materialId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(materialId));
        }

        var filePathName = Path.Combine(_applicationBasePath, "data", "MaterialMapping.xlsx");

        if (!File.Exists(filePathName))
        {
            throw new Exception($"Data/MaterialMapping.xlsx not found.. Expected at: {filePathName}");
        }

        XLWorkbook workbook;
        using (var fileStream = new FileStream(filePathName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            workbook = new XLWorkbook(fileStream);
        }

        var worksheet = workbook.Worksheets.FirstOrDefault();

        // select range with used data in the sheet
        var rangeUsed = worksheet.RangeUsed();

        // first column has the Material id's
        var column1 = rangeUsed.Column(1);
        // search for the
        var foundCells = column1.Search(materialId, CompareOptions.OrdinalIgnoreCase);
        if (foundCells != null && foundCells.Any())
        {
            var firstCell = foundCells.FirstOrDefault();
            var rowNumber = firstCell.Address.RowNumber;
            // get the integration Material id
            var column2 = rangeUsed.Column(2);
            return column2.Cell(rowNumber).Value.ToString();
        }

        return string.Empty;
    }


    private IEnumerable<MaterialMapModel> GetMaterialMapListForGroupKeywordMapping()
    {
        var filePathName = Path.Combine(_applicationBasePath, "data", "MaterialMapping.xlsx");

        if (!File.Exists(filePathName))
        {
            throw new Exception($"Data/MaterialMapping.xlsx not found.. Expected at: {filePathName}");
        }

        XLWorkbook workbook;
        using (var fileStream = new FileStream(filePathName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            workbook = new XLWorkbook(fileStream);
        }

        var worksheet = workbook.Worksheets.FirstOrDefault();

        // select range with used data in the sheet
        var rangeUsed = worksheet.RangeUsed();

        var colMaterialGroup = 1;
        var colKeyword = 2;
        var bySoftCamMaterialCode = 3;

        // Get all rows where all 3 cells have a value
        var mappingRows = rangeUsed.AsTable().DataRange.Rows()
            .Where(row => row.Cell(colMaterialGroup).GetString().Trim().Length > 0
                          && row.Cell(colKeyword).GetString().Trim().Length > 0
                          && row.Cell(bySoftCamMaterialCode).GetString().Trim().Length > 0);

        return mappingRows
            .Select(xlTableRow =>
                new MaterialMapModel
                {
                    MaterialGroup = xlTableRow.Cell(colMaterialGroup).Value.ToString(),
                    Keyword = xlTableRow.Cell(colKeyword).Value.ToString(),
                    BySoftCamMaterialCode = xlTableRow.Cell(bySoftCamMaterialCode).Value.ToString()
                }).ToArray();
    }

    private string GetMaterialIdFromKeywords(IEnumerable<string> keywords, IEnumerable<MaterialMapModel> mappingList)
    {
        // Find mapping rows where the keywords map with the material group
        var inGroup = mappingList.Where(m => keywords.Contains(m.MaterialGroup, StringComparer.OrdinalIgnoreCase)).ToList();
        if (inGroup.Any())
        {
            // Next find the rows where the mapping-keyword maps with the keywords
            var foundKeyword = inGroup.Where(k => keywords.Contains(k.Keyword, StringComparer.OrdinalIgnoreCase)).ToList();
            if (foundKeyword.Any())
            {
                return foundKeyword.First().BySoftCamMaterialCode;
            }
        }

        return string.Empty;
    }
}
