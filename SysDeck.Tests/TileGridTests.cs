using SysDeck.Deck;
using SysDeck.Models;
using Xunit;

namespace SysDeck.Tests;

public class TileGridTests
{
    private static List<ShortcutItem> Items(int count)
        => Enumerable.Range(0, count).Select(i => new ShortcutItem { Name = "T" + i }).ToList();

    [Theory]
    [InlineData(1240, 120, 9)]   // 1240 / 132 = 9
    [InlineData(2400, 60, 16)]   // 33 -> borné à 16
    [InlineData(400, 120, 3)]
    [InlineData(10, 120, 1)]     // au moins 1
    [InlineData(132, 120, 1)]
    [InlineData(264, 120, 2)]
    public void Columns_sont_bornees(double width, double tile, int expected)
        => Assert.Equal(expected, TileGrid.Columns(width, tile));

    [Theory]
    [InlineData(1240, 120, 4, 4)]
    [InlineData(400, 120, 5, 3)]
    [InlineData(1240, 120, 0, 9)]
    public void Colonnes_preferees_respectent_la_largeur_disponible(double width, double tile, int preferred, int expected)
        => Assert.Equal(expected, TileGrid.Columns(width, tile, preferred));

    [Theory]
    [InlineData(720, 120, 5)]
    [InlineData(1000, 60, 9)]    // 13 -> borné à 9
    [InlineData(160, 120, 1)]
    [InlineData(300, 120, 2)]
    [InlineData(1, 120, 1)]
    public void Rows_sont_bornees(double height, double tile, int expected)
        => Assert.Equal(expected, TileGrid.Rows(height, tile));

    [Fact]
    public void Layout_repartit_la_place_restante_entre_les_cases()
    {
        // 1220 / 109 = 11 colonnes (reste 21 px), 610 / 109 = 5 lignes (reste 65 px) : plus de vide d'un seul côté.
        var layout = TileGrid.Layout(1220, 610, 11, 5, 97);
        Assert.Equal(1220 / 11.0, layout.PitchX, 3);
        Assert.Equal(610 / 5.0, layout.PitchY, 3);
        Assert.Equal(0, layout.OffsetX, 3);
        Assert.Equal(10, layout.ColAt(1219));
        Assert.Equal(4, layout.RowAt(609));
    }

    [Fact]
    public void Layout_centre_quand_l_ecart_serait_trop_grand()
    {
        // 2 colonnes imposées dans un bloc de 1000 px : écart plafonné, grille centrée.
        var layout = TileGrid.Layout(1000, 132, 2, 1, 120);
        Assert.Equal(132 + 60, layout.PitchX, 3);
        Assert.Equal((1000 - 2 * 192) / 2.0, layout.OffsetX, 3);
    }

    [Fact]
    public void Ancienne_liste_sans_position_est_placee_de_gauche_a_droite_puis_de_haut_en_bas()
    {
        var items = Items(5);
        var a = TileGrid.Arrange(items, 3, 2);
        Assert.Empty(a.Hidden);
        Assert.Equal((0, 0), a.Cells[items[0]]);
        Assert.Equal((2, 0), a.Cells[items[2]]);
        Assert.Equal((0, 1), a.Cells[items[3]]);
        Assert.Equal((1, 1), a.Cells[items[4]]);
        Assert.All(items, i => Assert.Null(i.GridCol)); // rien n'est écrit avant Commit
    }

    [Fact]
    public void Commit_enregistre_les_positions()
    {
        var items = Items(3);
        TileGrid.Commit(items, 2, 2);
        Assert.Equal((0, 0), (items[0].GridCol, items[0].GridRow));
        Assert.Equal((0, 1), (items[2].GridCol, items[2].GridRow));
    }

    [Fact]
    public void Cases_vides_conservees()
    {
        var items = Items(2);
        items[0].GridCol = 3; items[0].GridRow = 1;
        var a = TileGrid.Arrange(items, 4, 2);
        Assert.Equal((3, 1), a.Cells[items[0]]);
        Assert.Equal((0, 0), a.Cells[items[1]]); // première case libre
    }

    [Fact]
    public void Reduction_replace_dans_la_premiere_case_libre()
    {
        var items = Items(2);
        items[0].GridCol = 0; items[0].GridRow = 0;
        items[1].GridCol = 5; items[1].GridRow = 0;
        var a = TileGrid.Arrange(items, 3, 1);
        Assert.Empty(a.Hidden);
        Assert.Equal((0, 0), a.Cells[items[0]]);
        Assert.Equal((1, 0), a.Cells[items[1]]);
    }

    [Fact]
    public void Reduction_sans_case_libre_masque_la_tuile_sans_perte()
    {
        var items = Items(3);
        TileGrid.Commit(items, 3, 1);
        var a = TileGrid.Arrange(items, 2, 1);
        Assert.Single(a.Hidden);
        Assert.Same(items[2], a.Hidden[0]);
        Assert.Equal(3, items.Count);
        // Agrandi de nouveau (sans Commit entre-temps) : elle reprend sa place.
        Assert.Equal((2, 0), TileGrid.Arrange(items, 3, 1).Cells[items[2]]);
    }

    [Fact]
    public void Conflit_de_position_deplace_la_seconde_tuile()
    {
        var items = Items(2);
        foreach (var i in items) { i.GridCol = 0; i.GridRow = 0; }
        var a = TileGrid.Arrange(items, 2, 1);
        Assert.Equal((0, 0), a.Cells[items[0]]);
        Assert.Equal((1, 0), a.Cells[items[1]]);
    }

    [Fact]
    public void Echange_de_deux_tuiles()
    {
        var items = Items(2);
        TileGrid.Commit(items, 4, 1);
        Assert.True(TileGrid.MoveOrSwap(items, 4, 1, items[0], 1, 0));
        Assert.Equal(1, items[0].GridCol);
        Assert.Equal(0, items[1].GridCol);
    }

    [Fact]
    public void Deplacement_vers_une_case_vide_et_hors_grille()
    {
        var items = Items(2);
        Assert.True(TileGrid.MoveOrSwap(items, 4, 1, items[0], 3, 0));
        Assert.Equal(3, items[0].GridCol);
        Assert.Equal(1, items[1].GridCol); // positions enregistrées par la modification
        Assert.False(TileGrid.MoveOrSwap(items, 4, 1, items[0], 4, 0));
    }

    [Fact]
    public void Premiere_case_libre_et_grille_pleine()
    {
        var items = Items(2);
        items[0].GridCol = 0; items[0].GridRow = 0;
        items[1].GridCol = 2; items[1].GridRow = 0;
        Assert.Equal((1, 0), TileGrid.FirstFree(items, 3, 1));
        items.Add(new ShortcutItem { GridCol = 1, GridRow = 0 });
        Assert.Null(TileGrid.FirstFree(items, 3, 1));
    }

    [Fact]
    public void Step_echange_avec_la_voisine_dans_l_ordre_de_lecture()
    {
        var items = Items(3);
        TileGrid.Commit(items, 2, 2);
        Assert.True(TileGrid.Step(items, 2, 2, items[1], 1)); // (1,0) -> (0,1), case vide
        Assert.Equal((0, 1), (items[1].GridCol, items[1].GridRow));
        Assert.False(TileGrid.Step(items, 2, 2, items[0], -1));
    }
}
